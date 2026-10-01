using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Adapters;
using PulseBot.Core.Commands;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;

namespace PulseBot.Core.Tests;

/// <summary>
/// 端到端链路测试：把接入控制、命令分发、权限判定与回复通道串成一条真实管道，
/// 验证「一条消息从事件入口进入到给出响应」的完整协作。
/// </summary>
/// <remarks>
/// 分层单元测试各自验证自己那一环，但接缝（谁先执行、谁阻断谁、消息被消费后去了哪里）
/// 只有把整条管道装起来才能验证——本文件补的就是这部分。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class EndToEndFlowTests
{
    /// <summary>授权用户执行受保护命令：命令执行、回复送达发送器、消息不再流向普通监听器。</summary>
    [Fact]
    public async Task 授权用户执行受保护命令并收到回复()
    {
        var harness = CreateHarness();
        await harness.Permissions.GrantAsync(PermissionSubject.User("u1"), "admin.ban", CancellationToken.None);

        await harness.Publisher.PublishAsync(CreateMessage("/ban 某人"), CancellationToken.None);

        Assert.Equal(["某人"], harness.Plugin.Invocations);
        Assert.Equal(["reply:msg-1:已封禁 某人"], harness.Sender.Replies);
        Assert.Empty(harness.Listener.Seen);
    }

    /// <summary>未授权用户被权限系统拒绝：插件不执行，收到拒绝提示，消息同样被消费。</summary>
    [Fact]
    public async Task 未授权用户被拒绝且收到提示()
    {
        var harness = CreateHarness();

        await harness.Publisher.PublishAsync(CreateMessage("/ban 某人"), CancellationToken.None);

        Assert.Empty(harness.Plugin.Invocations);
        Assert.Equal(["reply:msg-1:你没有权限执行该命令。"], harness.Sender.Replies);
        Assert.Empty(harness.Listener.Seen);
    }

    /// <summary>
    /// 黑名单用户即使已被授予权限也进不来：接入控制位于管道最前端，
    /// 在命令分发之前就阻断了传播，因此连权限提示都不会发出。
    /// </summary>
    [Fact]
    public async Task 黑名单用户在命令分发之前就被拦截()
    {
        var harness = CreateHarness(new PermissionOptions { UserBlacklist = ["evil"] });
        await harness.Permissions.GrantAsync(PermissionSubject.User("evil"), "admin.ban", CancellationToken.None);

        await harness.Publisher.PublishAsync(CreateMessage("/ban 某人", senderId: "evil"), CancellationToken.None);

        Assert.Empty(harness.Plugin.Invocations);
        Assert.Empty(harness.Sender.Replies);
        Assert.Empty(harness.Listener.Seen);
    }

    /// <summary>普通聊天不命中任何命令，正常流向监听器（对照组）。</summary>
    [Fact]
    public async Task 普通聊天消息流向监听器()
    {
        var harness = CreateHarness();

        await harness.Publisher.PublishAsync(CreateMessage("今天天气不错"), CancellationToken.None);

        Assert.Equal(["今天天气不错"], harness.Listener.Seen);
        Assert.Empty(harness.Plugin.Invocations);
    }

    // ---------- 测试基础设施 ----------

    /// <summary>
    /// 组装一条完整的消息管道：事件入口 → 接入控制 → 命令分发 → 普通监听器。
    /// </summary>
    /// <remarks>
    /// 处理器**故意不按优先级顺序**传入：排序由 <see cref="HandlerRegistration{TEvent}"/> 负责，
    /// 若它失效，本组测试会立刻失败——这正是端到端测试该覆盖的接缝。
    /// </remarks>
    private static Harness CreateHarness(PermissionOptions? options = null)
    {
        var permissionOptions = options ?? new PermissionOptions();
        var permissions = new InMemoryPermissionService(
            Options.Create(permissionOptions),
            NullLogger<InMemoryPermissionService>.Instance);

        var plugin = new DemoPlugin();
        var commandDispatcher = new CommandDispatcher(
            Options.Create(new CommandOptions()),
            permissions,
            NullLogger<CommandDispatcher>.Instance);
        commandDispatcher.RegisterPluginCommands(plugin);

        var listener = new RecordingListener();
        IEventHandler<MessageEvent>[] handlers =
        [
            listener,                                                                  // 优先级 0
            new CommandMessageHandler(commandDispatcher),                               // int.MaxValue - 2
            new AccessControlHandler(                                                   // int.MaxValue
                Options.Create(permissionOptions),
                NullLogger<AccessControlHandler>.Instance),
        ];

        var sender = new FakeSender();
        var publisher = new EventPublisher(
            new EventDispatcher(
                [new HandlerRegistration<MessageEvent>(handlers)],
                NullLogger<EventDispatcher>.Instance),
            () => sender);

        return new Harness(publisher, sender, plugin, listener, permissions);
    }

    private static MessageEvent CreateMessage(string text, string senderId = "u1") => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, senderId),
        Message = Message.Text(text),
        MessageId = "msg-1",
        SenderId = senderId,
    };

    /// <summary>一次装配好的管道，以及其中可供断言的对象。</summary>
    private sealed record Harness(
        IEventPublisher Publisher,
        FakeSender Sender,
        DemoPlugin Plugin,
        RecordingListener Listener,
        InMemoryPermissionService Permissions);

    /// <summary>受权限保护的示例插件，执行时向消息来源回复。</summary>
    private sealed class DemoPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        [OnCommand("ban", Aliases = ["封禁"])]
        [RequirePermission("admin.ban")]
        public async ValueTask BanAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.ArgsText);
            await context.ReplyAsync($"已封禁 {context.ArgsText}", cancellationToken);
        }
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
}
