using Microsoft.Extensions.Logging.Abstractions;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Core.Tests;

/// <summary>
/// 事件分发器行为测试：优先级、阻断、异常隔离与类型匹配。
/// </summary>
[Trait("Category", "Unit")]
public sealed class EventDispatcherTests
{
    /// <summary>优先级数值大者先执行。</summary>
    [Fact]
    public async Task 分发时按优先级从高到低调用处理器()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            new RecordingHandler(log, "low") { Priority = -5 },
            new RecordingHandler(log, "high") { Priority = 100 },
            new RecordingHandler(log, "default"));

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["high", "default", "low"], log);
    }

    /// <summary>同优先级的处理器按注册顺序执行（稳定排序）。</summary>
    [Fact]
    public async Task 同优先级的处理器按注册顺序执行()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            new RecordingHandler(log, "first") { Priority = 1 },
            new RecordingHandler(log, "second") { Priority = 1 },
            new RecordingHandler(log, "third") { Priority = 1 });

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["first", "second", "third"], log);
    }

    /// <summary>调用 StopPropagation 后，后续处理器不再执行（当前处理器自身正常完成）。</summary>
    [Fact]
    public async Task 阻断传播后不再调用后续处理器()
    {
        var log = new List<string>();
        var blocker = new RecordingHandler(log, "blocker") { Priority = 10, StopAfterHandling = true };
        EventDispatcher dispatcher = CreateDispatcher(
            blocker,
            new RecordingHandler(log, "after-1"),
            new RecordingHandler(log, "after-2") { Priority = -1 });

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["blocker"], log);
    }

    /// <summary>单个处理器抛异常不会中断管道，其余处理器照常执行。</summary>
    [Fact]
    public async Task 处理器异常被隔离且不影响其它处理器()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            new RecordingHandler(log, "before") { Priority = 10 },
            new RecordingHandler(log, "thrower") { Priority = 5, ThrowOnHandle = true },
            new RecordingHandler(log, "after"));

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["before", "thrower", "after"], log);
    }

    /// <summary>没有注册任何处理器时，分发是安全的空操作。</summary>
    [Fact]
    public async Task 无处理器时分发不抛异常()
    {
        EventDispatcher dispatcher = CreateDispatcher();

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);
    }

    /// <summary>只匹配运行时类型：注册 MessageEvent 的处理器不会收到其它事件类型。</summary>
    [Fact]
    public async Task 只调用匹配事件类型的处理器()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = new(
            [
                new HandlerRegistration<MessageEvent>([new RecordingHandler(log, "message")]),
                new HandlerRegistration<OtherEvent>([new OtherRecordingHandler(log)]),
            ],
            NullLogger<EventDispatcher>.Instance);

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["message"], log);
    }

    /// <summary>宿主关闭（令牌已取消）时的取消信号正常向上传播，不被当作处理器异常吞掉。</summary>
    [Fact]
    public async Task 宿主取消信号向上传播()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        EventDispatcher dispatcher = CreateDispatcher(new CancelAwareHandler(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await dispatcher.DispatchAsync(CreateMessageEvent(), cts.Token));
    }

    // ---------- 测试基础设施 ----------

    private static EventDispatcher CreateDispatcher(params IEventHandler<MessageEvent>[] handlers) =>
        new(
            [new HandlerRegistration<MessageEvent>(handlers)],
            NullLogger<EventDispatcher>.Instance);

    private static MessageEvent CreateMessageEvent() => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, "user-1"),
        Message = Message.Text("你好"),
        MessageId = "msg-1",
        SenderId = "user-1",
    };

    /// <summary>记录调用顺序的处理器；可设置抛出异常或阻断传播。</summary>
    private sealed class RecordingHandler(List<string> log, string name) : IEventHandler<MessageEvent>
    {
        public int Priority { get; init; }

        public bool ThrowOnHandle { get; init; }

        public bool StopAfterHandling { get; init; }

        public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            log.Add(name);

            if (StopAfterHandling)
            {
                context.StopPropagation();
            }

            if (ThrowOnHandle)
            {
                throw new InvalidOperationException($"处理器 {name} 故意失败");
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>仅在被调用时记录，用于验证事件类型不匹配时不会被调用。</summary>
    private sealed class OtherRecordingHandler(List<string> log) : IEventHandler<OtherEvent>
    {
        public ValueTask HandleAsync(OtherEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            log.Add("other");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>在被取消的令牌下抛出取消异常，模拟宿主关闭。</summary>
    private sealed class CancelAwareHandler(CancellationToken hostToken) : IEventHandler<MessageEvent>
    {
        public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            hostToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>用于验证类型匹配的哑事件。</summary>
    private sealed record OtherEvent : IEvent;
}
