using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AnBot.Core.Dispatching;
using AnBot.Core.Events;
using AnBot.Core.Messages;

namespace AnBot.Core.Tests;

/// <summary>
/// 事件中间件管道测试：洋葱顺序、事件改写、短路、异常隔离与注册幂等。
/// </summary>
/// <remarks>
/// 中间件与处理器是两层机制，本文件只钉中间件这一层的语义；
/// 处理器之间的优先级与阻断仍由 <see cref="EventDispatcherTests"/> 覆盖。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class EventMiddlewareTests
{
    /// <summary>中间件按优先级由外到内包裹处理器：前处理正序进入，后处理逆序返回。</summary>
    [Fact]
    public async Task 中间件按洋葱顺序包裹处理器()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            [new CapturingHandler(log)],
            [
                new RecordingMiddleware(log, "inner"),                      // 优先级 0
                new RecordingMiddleware(log, "outer") { Priority = 10 },
            ]);

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["enter:outer", "enter:inner", "handler", "exit:inner", "exit:outer"], log);
    }

    /// <summary>同优先级的中间件按注册顺序由外到内进入。</summary>
    [Fact]
    public async Task 同优先级的中间件按注册顺序进入()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            [new CapturingHandler(log)],
            [
                new RecordingMiddleware(log, "first"),
                new RecordingMiddleware(log, "second"),
            ]);

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["enter:first", "enter:second", "handler", "exit:second", "exit:first"], log);
    }

    /// <summary>不调用续延即短路：其后的中间件与处理器都不会执行。</summary>
    [Fact]
    public async Task 中间件不调用续延则短路()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            [new CapturingHandler(log)],
            [
                new RecordingMiddleware(log, "blocker") { Priority = 10, ShortCircuit = true },
                new RecordingMiddleware(log, "never-reached"),
            ]);

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["enter:blocker", "short:blocker"], log);
    }

    /// <summary>中间件改写后的事件会替代原实例继续向下传递。</summary>
    [Fact]
    public async Task 中间件改写的事件传递给处理器()
    {
        var log = new List<string>();
        var handler = new CapturingHandler(log);
        MessageEvent original = CreateMessageEvent();
        MessageEvent replaced = original with { MessageId = "改写后" };

        EventDispatcher dispatcher = CreateDispatcher(
            [handler],
            [new RecordingMiddleware(log, "rewriter") { ReplacedEvent = replaced }]);

        await dispatcher.DispatchAsync(original, CancellationToken.None);

        Assert.Same(replaced, Assert.Single(handler.Seen));
    }

    /// <summary>中间件与处理器共享同一个分发上下文实例。</summary>
    [Fact]
    public async Task 中间件与处理器共享同一个上下文()
    {
        var log = new List<string>();
        var handler = new CapturingHandler(log);
        var middleware = new RecordingMiddleware(log, "捕捉上下文");

        EventDispatcher dispatcher = CreateDispatcher([handler], [middleware]);

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.NotNull(middleware.CapturedContext);
        Assert.Same(middleware.CapturedContext, handler.CapturedContext);
    }

    /// <summary>
    /// 中间件异常被隔离：不外传，且异常点之后的管道自然终止（处理器不执行）。
    /// </summary>
    [Fact]
    public async Task 中间件异常被隔离且终止管道()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = CreateDispatcher(
            [new CapturingHandler(log)],
            [
                new RecordingMiddleware(log, "thrower") { Priority = 10, ThrowOnInvoke = true },
                new RecordingMiddleware(log, "inner"),
            ]);

        // 不外传：调用点不会看到异常。
        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["enter:thrower"], log);
    }

    /// <summary>没有注册任何处理器时，中间件仍然会执行（审计这类中间件要看到全部事件）。</summary>
    [Fact]
    public async Task 没有处理器时中间件仍会执行()
    {
        var log = new List<string>();
        EventDispatcher dispatcher = new(
            [],
            [new RecordingMiddleware(log, "auditor")],
            NullLogger<EventDispatcher>.Instance);

        await dispatcher.DispatchAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.Equal(["enter:auditor", "exit:auditor"], log);
    }

    /// <summary>宿主关闭（令牌已取消）时的取消信号穿过中间件正常向上传播。</summary>
    [Fact]
    public async Task 宿主取消信号穿过中间件向上传播()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        EventDispatcher dispatcher = CreateDispatcher(
            [new CancelAwareHandler(cts.Token)],
            [new RecordingMiddleware(new List<string>(), "passthrough")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await dispatcher.DispatchAsync(CreateMessageEvent(), cts.Token));
    }

    /// <summary>只关心消息事件的中间件基类：其它事件类型直接放行，交到处理器手里。</summary>
    [Fact]
    public async Task 消息中间件对非消息事件直接放行()
    {
        var log = new List<string>();
        var handler = new OtherEventHandler(log);
        var middleware = new StubMessageMiddleware();

        EventDispatcher dispatcher = new(
            [new HandlerRegistration<OtherEvent>([handler])],
            [middleware],
            NullLogger<EventDispatcher>.Instance);

        await dispatcher.DispatchAsync(new OtherEvent(), CancellationToken.None);

        Assert.False(middleware.Invoked);
        Assert.True(handler.Handled);
    }

    /// <summary>重复注册同一个中间件是幂等的，它只会被执行一次。</summary>
    [Fact]
    public void 重复注册中间件是幂等的()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddEventMiddleware<PassthroughMiddleware>();
        services.AddEventMiddleware<PassthroughMiddleware>();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IEventMiddleware>().OfType<PassthroughMiddleware>());
    }

    // ---------- 测试基础设施 ----------

    private static EventDispatcher CreateDispatcher(
        IEventHandler<MessageEvent>[] handlers,
        IEventMiddleware[] middlewares)
        => new(
            [new HandlerRegistration<MessageEvent>(handlers)],
            middlewares,
            NullLogger<EventDispatcher>.Instance);

    private static MessageEvent CreateMessageEvent() => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, "user-1"),
        Message = Message.Text("你好"),
        MessageId = "msg-1",
        SenderId = "user-1",
    };

    /// <summary>记录进入/退出/短路的中间件；可改写事件、短路或抛异常。</summary>
    private sealed class RecordingMiddleware(List<string> log, string name) : IEventMiddleware
    {
        public int Priority { get; init; }

        public bool ShortCircuit { get; init; }

        public bool ThrowOnInvoke { get; init; }

        /// <summary>不为 null 时用它替换原事件传给下游。</summary>
        public IEvent? ReplacedEvent { get; init; }

        public EventContext? CapturedContext { get; private set; }

        public async ValueTask InvokeAsync(
            IEvent @event,
            EventContext context,
            EventMiddlewareNext next,
            CancellationToken cancellationToken)
        {
            log.Add($"enter:{name}");
            CapturedContext = context;

            if (ThrowOnInvoke)
            {
                throw new InvalidOperationException($"中间件 {name} 故意失败");
            }

            if (ShortCircuit)
            {
                log.Add($"short:{name}");
                return;
            }

            await next(ReplacedEvent ?? @event, context, cancellationToken).ConfigureAwait(false);
            log.Add($"exit:{name}");
        }
    }

    /// <summary>记录收到的事件实例与上下文。</summary>
    private sealed class CapturingHandler(List<string> log) : IEventHandler<MessageEvent>
    {
        public List<MessageEvent> Seen { get; } = [];

        public EventContext? CapturedContext { get; private set; }

        public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            log.Add("handler");
            Seen.Add(@event);
            CapturedContext = context;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>仅记录是否被调用的非消息事件处理器。</summary>
    private sealed class OtherEventHandler(List<string> log) : IEventHandler<OtherEvent>
    {
        public bool Handled { get; private set; }

        public ValueTask HandleAsync(OtherEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            log.Add("other-handler");
            Handled = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>记录自己是否被调用的消息中间件；用于验证非消息事件不会进入它。</summary>
    private sealed class StubMessageMiddleware : MessageEventMiddleware
    {
        public bool Invoked { get; private set; }

        public override ValueTask InvokeAsync(
            MessageEvent @event,
            EventContext context,
            EventMiddlewareNext next,
            CancellationToken cancellationToken)
        {
            Invoked = true;
            return next(@event, context, cancellationToken);
        }
    }

    /// <summary>原样放行的最小中间件；仅用于验证 DI 注册的幂等性。</summary>
    private sealed class PassthroughMiddleware : IEventMiddleware
    {
        public ValueTask InvokeAsync(
            IEvent @event,
            EventContext context,
            EventMiddlewareNext next,
            CancellationToken cancellationToken)
            => next(@event, context, cancellationToken);
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
