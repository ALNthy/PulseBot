using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Adapters;
using PulseBot.Core.Commands;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;
using PulseBot.Core.Sessions;

namespace PulseBot.Core.Tests;

/// <summary>
/// 等待态拦截器测试：消息如何被路由回提问的插件、超时与取消的处理，
/// 以及它在管道中的优先级位置。
/// </summary>
/// <remarks>
/// 测试一律走真实事件入口，通过「消息是否流到了普通监听器」来判断传播是否被阻断——
/// 这比直接调用处理器更接近真实语义（直接调用会绕过传播机制本身）。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SessionPromptTests
{
    /// <summary>登记等待后，下一条消息交给回调处理，并被拦下、不再流向监听器。</summary>
    [Fact]
    public async Task 登记等待后下一条消息被回调接收并阻断传播()
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", (reply, _) =>
        {
            received.Add(reply.Message.PlainText);
            return ValueTask.CompletedTask;
        });

        await harness.PublishAsync(CreateMessage("小明"));

        Assert.Equal(["小明"], received);
        Assert.Empty(harness.Listener.Seen);
    }

    /// <summary>回调拿到的上下文能读出发送者，也能直接回复。</summary>
    [Fact]
    public async Task 回调上下文携带用户消息并可回复()
    {
        var harness = CreateHarness();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", static async (reply, cancellationToken) =>
        {
            await reply.ReplyAsync($"收到 {reply.Message.SenderId} 的回答", cancellationToken);
        });

        await harness.PublishAsync(CreateMessage("小明"));

        Assert.Equal(["reply:msg-1:收到 u1 的回答"], harness.Sender.Replies);
    }

    /// <summary>没有等待态时拦截器不做任何事，消息照常流向普通监听器。</summary>
    [Fact]
    public async Task 没有等待态时消息流向其它处理器()
    {
        var harness = CreateHarness();

        // 会话存在，但没有登记等待。
        await harness.GetOrCreateAsync("u1");

        await harness.PublishAsync(CreateMessage("随便聊聊"));

        Assert.Equal(["随便聊聊"], harness.Listener.Seen);
    }

    /// <summary>等待态是一次性的：回答过后，再来的消息回到正常管道。</summary>
    [Fact]
    public async Task 等待态被消费后第二条消息回到正常管道()
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", (reply, _) =>
        {
            received.Add(reply.Message.PlainText);
            return ValueTask.CompletedTask;
        });

        await harness.PublishAsync(CreateMessage("第一次"));
        await harness.PublishAsync(CreateMessage("第二次"));

        Assert.Equal(["第一次"], received);
        Assert.Equal(["第二次"], harness.Listener.Seen);
    }

    /// <summary>等待超过阈值后，迟到的消息按普通消息放行，而不是被当成回答吞掉。</summary>
    [Fact]
    public async Task 等待态超时后消息被放行()
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", (reply, _) =>
        {
            received.Add(reply.Message.PlainText);
            return ValueTask.CompletedTask;
        });

        harness.Time.Advance(TimeSpan.FromMinutes(6));

        await harness.PublishAsync(CreateMessage("刚才那个怎么弄"));

        Assert.Empty(received);
        Assert.Equal(["刚才那个怎么弄"], harness.Listener.Seen);
    }

    /// <summary>取消词清除等待态、回复提示，且不触发回调，消息也不再流向后续处理器。</summary>
    [Fact]
    public async Task 取消词清除等待态并回复()
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", (reply, _) =>
        {
            received.Add(reply.Message.PlainText);
            return ValueTask.CompletedTask;
        });

        await harness.PublishAsync(CreateMessage("/cancel"));

        Assert.Empty(received);
        Assert.Empty(harness.Listener.Seen);
        Assert.Equal(["reply:msg-1:已取消。"], harness.Sender.Replies);

        // 等待态已清除：随后的普通消息不会被当成回答。
        await harness.PublishAsync(CreateMessage("接着说"));
        Assert.Equal(["接着说"], harness.Listener.Seen);
    }

    /// <summary>取消词只做精确匹配，形如「/cancel 理由」的输入仍然算回答。</summary>
    [Fact]
    public async Task 取消词只做精确匹配()
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", (reply, _) =>
        {
            received.Add(reply.Message.PlainText);
            return ValueTask.CompletedTask;
        });

        await harness.PublishAsync(CreateMessage("/cancel 我不想说"));

        Assert.Equal(["/cancel 我不想说"], received);
        Assert.Empty(harness.Sender.Replies);
    }

    /// <summary>
    /// 回调内部可以继续提问：拦截器必须在调用回调**之前**就取走等待态，
    /// 否则回调刚登记的新等待态会被随后的清理抹掉，第二轮永远收不到回答。
    /// </summary>
    [Fact]
    public async Task 回调内再次提问可继续下一轮()
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("第一轮", (reply, _) =>
        {
            received.Add($"第一轮:{reply.Message.PlainText}");

            reply.Session.BeginPrompt("第二轮", (inner, _) =>
            {
                received.Add($"第二轮:{inner.Message.PlainText}");
                return ValueTask.CompletedTask;
            });

            return ValueTask.CompletedTask;
        });

        await harness.PublishAsync(CreateMessage("答案一"));
        await harness.PublishAsync(CreateMessage("答案二"));

        Assert.Equal(["第一轮:答案一", "第二轮:答案二"], received);
    }

    /// <summary>
    /// 回调抛异常时消息不会回流：异常由 EventDispatcher 统一隔离并记录，
    /// 但因为等待态在调用回调之前就被取走、传播也已阻断，普通监听器不会再看到这条消息。
    /// </summary>
    [Fact]
    public async Task 回调抛异常时消息也不会回流()
    {
        var harness = CreateHarness();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", ThrowingHandler);

        await harness.PublishAsync(CreateMessage("你好"));

        Assert.Empty(harness.Listener.Seen);
        Assert.Empty(harness.Sender.Replies);
    }

    /// <summary>三档优先级必须严格递减：接入控制 &gt; 会话拦截 &gt; 命令分发。</summary>
    [Fact]
    public void 会话拦截优先级介于接入控制与命令分发之间()
    {
        var accessPriority = new AccessControlHandler(
            Options.Create(new PermissionOptions()),
            NullLogger<AccessControlHandler>.Instance).Priority;

        var sessionPriority = CreateHandler(new FakeTimeProvider(StartTime)).Priority;

        var commandPriority = new CommandMessageHandler(new NoopCommandDispatcher()).Priority;

        Assert.True(accessPriority > sessionPriority);
        Assert.True(sessionPriority > commandPriority);
    }

    /// <summary>
    /// 防回归：处理器按「监听器 → 会话拦截 → 接入控制」的乱序传入时，接入控制仍须最先执行。
    /// 若排序失效，被拉黑用户的消息会先被会话拦截消费掉——这是静默的安全边界击穿，
    /// 不会留下任何报错，必须由测试钉死。
    /// </summary>
    [Fact]
    public async Task 乱序注册时接入控制仍先于会话拦截()
    {
        var sessionOptions = new SessionOptions();
        var permissionOptions = new PermissionOptions { UserBlacklist = ["evil"] };
        var time = new FakeTimeProvider(StartTime);

        var store = new InMemorySessionStore(
            Options.Create(sessionOptions),
            time,
            NullLogger<InMemorySessionStore>.Instance);

        // 预先造出一个等待态：否则本测试只能证明「黑名单拦截一切」，
        // 而证明不了「接入控制先于会话拦截执行」。
        Session session = await store.GetOrCreateAsync(CreateKey("evil"), CancellationToken.None);

        var callbackInvoked = false;
        session.BeginPrompt("测试", (_, _) =>
        {
            callbackInvoked = true;
            return ValueTask.CompletedTask;
        });

        var listener = new RecordingListener();
        IEventHandler<MessageEvent>[] handlers =
        [
            listener,                                                       // 优先级 0
            CreateHandler(time, store, sessionOptions),                      // int.MaxValue - 1
            new AccessControlHandler(                                        // int.MaxValue
                Options.Create(permissionOptions),
                NullLogger<AccessControlHandler>.Instance),
        ];

        var sender = new FakeSender();
        var publisher = new EventPublisher(
            new EventDispatcher(
                [new HandlerRegistration<MessageEvent>(handlers)],
                NullLogger<EventDispatcher>.Instance),
            () => sender);

        await publisher.PublishAsync(CreateMessage("你好", "evil"), CancellationToken.None);

        Assert.False(callbackInvoked);
        Assert.Empty(sender.Replies);
        Assert.Empty(listener.Seen);
    }

    /// <summary>
    /// 等待回答期间，「回答」按回答处理而不是按命令处理。
    /// 无前缀命令默认开启，像「复读 你好」这种回答若不被拦下，会被命令分发夺走且毫无报错。
    /// </summary>
    [Theory]
    [InlineData("/echo 你好")]   // 带前缀
    [InlineData("复读 你好")]     // 无前缀模式下按命令名开头
    public async Task 回答形如命令时仍被当作回答(string replyText)
    {
        var harness = CreateHarness();
        var received = new List<string>();

        Session session = await harness.GetOrCreateAsync("u1");
        session.BeginPrompt("测试", (reply, _) =>
        {
            received.Add(reply.Message.PlainText);
            return ValueTask.CompletedTask;
        });

        await harness.PublishAsync(CreateMessage(replyText));

        Assert.Equal([replyText], received);
        Assert.Empty(harness.Listener.Seen);
    }

    // ---------- 测试基础设施 ----------

    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 组装「事件入口 → 会话拦截 → 普通监听器」的最小管道。
    /// 处理器**故意不按优先级顺序**传入，排序由 <see cref="HandlerRegistration{TEvent}"/> 负责。
    /// </summary>
    private static Harness CreateHarness(SessionOptions? options = null)
    {
        var sessionOptions = options ?? new SessionOptions();
        var time = new FakeTimeProvider(StartTime);

        var store = new InMemorySessionStore(
            Options.Create(sessionOptions),
            time,
            NullLogger<InMemorySessionStore>.Instance);

        SessionPromptHandler handler = CreateHandler(time, store, sessionOptions);
        var listener = new RecordingListener();
        var sender = new FakeSender();

        IEventHandler<MessageEvent>[] handlers = [listener, handler];

        var dispatcher = new EventDispatcher(
            [new HandlerRegistration<MessageEvent>(handlers)],
            NullLogger<EventDispatcher>.Instance);

        return new Harness(
            new EventPublisher(dispatcher, () => sender),
            handler,
            store,
            sender,
            listener,
            time);
    }

    private static SessionPromptHandler CreateHandler(
        TimeProvider timeProvider,
        InMemorySessionStore? store = null,
        SessionOptions? options = null)
    {
        var sessionOptions = options ?? new SessionOptions();

        return new SessionPromptHandler(
            store ?? new InMemorySessionStore(
                Options.Create(sessionOptions),
                timeProvider,
                NullLogger<InMemorySessionStore>.Instance),
            Options.Create(sessionOptions),
            NullLogger<SessionPromptHandler>.Instance);
    }

    private static SessionKey CreateKey(string userId)
        => new(new ConversationId("test", ConversationType.Private, "conv-1"), userId);

    private static MessageEvent CreateMessage(string text, string senderId = "u1") => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, "conv-1"),
        Message = Message.Text(text),
        MessageId = "msg-1",
        SenderId = senderId,
    };

    /// <summary>总是抛异常的问答回调：验证异常传播路径。</summary>
    private static ValueTask ThrowingHandler(SessionReplyContext reply, CancellationToken cancellationToken)
        => throw new InvalidOperationException("插件回调失败");

    /// <summary>一次装配好的管道，以及其中可供断言的对象。</summary>
    private sealed record Harness(
        IEventPublisher Publisher,
        SessionPromptHandler Handler,
        InMemorySessionStore Store,
        FakeSender Sender,
        RecordingListener Listener,
        FakeTimeProvider Time)
    {
        /// <summary>取得（或创建）某个用户在本会话中的交互会话。</summary>
        public ValueTask<Session> GetOrCreateAsync(string userId)
            => Store.GetOrCreateAsync(CreateKey(userId), CancellationToken.None);

        /// <summary>把一条消息送进完整管道（回复通道由事件入口自动装配）。</summary>
        public ValueTask PublishAsync(MessageEvent message)
            => Publisher.PublishAsync(message, CancellationToken.None);
    }

    /// <summary>普通消息监听器：记录它看到的每一条消息（默认优先级 0，排在管道最后）。</summary>
    private sealed class RecordingListener : IEventHandler<MessageEvent>
    {
        public List<string> Seen { get; } = [];

        public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
        {
            Seen.Add(@event.PlainText);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>仅用于比较优先级的空命令分发器。</summary>
    private sealed class NoopCommandDispatcher : ICommandDispatcher
    {
        public IReadOnlyCollection<CommandDescriptor> Commands => [];

        public ValueTask<bool> TryDispatchAsync(MessageEvent messageEvent, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
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

    /// <summary>可控时间源：只覆写 <see cref="GetUtcNow"/>，用 <see cref="Advance"/> 推进时间。</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
