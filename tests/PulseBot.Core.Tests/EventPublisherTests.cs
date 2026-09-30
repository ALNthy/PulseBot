using Microsoft.Extensions.Logging.Abstractions;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Core.Tests;

/// <summary>
/// 事件入口测试：回复通道装配、事件不可变性，以及「装配后回复真的能送达发送器」的闭环验证。
/// </summary>
[Trait("Category", "Unit")]
public sealed class EventPublisherTests
{
    /// <summary>适配器投递的纯数据消息事件会被装上回复通道，处理器拿到的副本具备回复能力。</summary>
    [Fact]
    public async Task 发布消息事件时自动装配回复通道()
    {
        var handler = new CapturingHandler();
        var sender = new FakeSender();
        EventPublisher publisher = CreatePublisher<MessageEvent>([handler], sender);
        var original = CreateMessageEvent();

        await publisher.PublishAsync(original, CancellationToken.None);

        Assert.Null(original.ReplyChannel);
        Assert.NotNull(handler.Captured);
        Assert.Same(sender, handler.Captured.ReplyChannel);
    }

    /// <summary>装配过回复通道的事件原样透传，不被覆盖。</summary>
    [Fact]
    public async Task 已有回复通道的事件不被覆盖()
    {
        var handler = new CapturingHandler();
        var sender = new FakeSender();
        var existingChannel = new FakeSender();
        EventPublisher publisher = CreatePublisher<MessageEvent>([handler], sender);
        var original = CreateMessageEvent() with { ReplyChannel = existingChannel };

        await publisher.PublishAsync(original, CancellationToken.None);

        Assert.NotNull(handler.Captured);
        Assert.Same(existingChannel, handler.Captured.ReplyChannel);
    }

    /// <summary>闭环验证：事件装配通道后调用 ReplyAsync，消息确实经由 IMessageSender 发出。</summary>
    [Fact]
    public async Task 装配后的回复通道能把消息送达发送器()
    {
        var handler = new CapturingHandler();
        var sender = new FakeSender();
        EventPublisher publisher = CreatePublisher<MessageEvent>([handler], sender);

        await publisher.PublishAsync(CreateMessageEvent(), CancellationToken.None);

        Assert.NotNull(handler.Captured);
        await handler.Captured.ReplyAsync("回显内容", CancellationToken.None);

        Assert.Equal(["reply:msg-1:回显内容"], sender.Replies);
    }

    /// <summary>非消息事件不做任何装配，正常分发。</summary>
    [Fact]
    public async Task 非消息事件正常分发()
    {
        var handler = new OtherEventHandler();
        var sender = new FakeSender();
        EventPublisher publisher = CreatePublisher<OtherEvent>([handler], sender);

        await publisher.PublishAsync(new OtherEvent(), CancellationToken.None);

        Assert.True(handler.Handled);
    }

    /// <summary>发布 null 事件属于编程错误，抛出参数异常。</summary>
    [Fact]
    public async Task 发布空事件抛出参数异常()
    {
        EventPublisher publisher = CreatePublisher<MessageEvent>([], new FakeSender());

        // 本测试专门验证参数校验，此处必须有 null；显式抑制可空警告。
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await publisher.PublishAsync(null!, CancellationToken.None));
    }

    // ---------- 测试基础设施 ----------

    private static EventPublisher CreatePublisher<TEvent>(
        IEventHandler<TEvent>[] handlers,
        IMessageSender sender)
        where TEvent : IEvent
        => new(
            new EventDispatcher(
                [new HandlerRegistration<TEvent>(handlers)],
                NullLogger<EventDispatcher>.Instance),
            () => sender);

    private static MessageEvent CreateMessageEvent() => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, "user-1"),
        Message = Message.Text("你好"),
        MessageId = "msg-1",
        SenderId = "user-1",
    };

    /// <summary>捕获分发到的事件实例，供断言检查。</summary>
    private sealed class CapturingHandler : IEventHandler<MessageEvent>
    {
        public MessageEvent? Captured { get; private set; }

        public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            Captured = @event;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>仅记录是否被调用的非消息事件处理器。</summary>
    private sealed class OtherEventHandler : IEventHandler<OtherEvent>
    {
        public bool Handled { get; private set; }

        public ValueTask HandleAsync(OtherEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            Handled = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>记录发送内容的发送器替身。</summary>
    private sealed class FakeSender : IMessageSender
    {
        public List<string> Replies { get; } = [];

        public ValueTask<SendResult> SendAsync(
            ConversationId conversation,
            Message message,
            CancellationToken cancellationToken)
        {
            Replies.Add($"send:{message}");
            return ValueTask.FromResult(SendResult.Ok());
        }

        public ValueTask<SendResult> ReplyAsync(
            ConversationId conversation,
            string replyToMessageId,
            Message message,
            CancellationToken cancellationToken)
        {
            Replies.Add($"reply:{replyToMessageId}:{message}");
            return ValueTask.FromResult(SendResult.Ok());
        }
    }

    /// <summary>用于验证非消息事件路径的哑事件。</summary>
    private sealed record OtherEvent : IEvent;
}
