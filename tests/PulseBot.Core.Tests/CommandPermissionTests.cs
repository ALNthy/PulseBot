using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Adapters;
using PulseBot.Core.Commands;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;

namespace PulseBot.Core.Tests;

/// <summary>
/// 命令与权限的集成测试：声明了 [RequirePermission] 的命令在权限不足时的行为。
/// </summary>
[Trait("Category", "Unit")]
public sealed class CommandPermissionTests
{
    /// <summary>未授予权限时命令不执行，但消息仍被视为已消费（不再流向下游插件）。</summary>
    [Fact]
    public async Task 权限不足时命令不执行但消息被消费()
    {
        var plugin = new ProtectedPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin, CreateService());

        var hit = await dispatcher.TryDispatchAsync(CreateMessageEvent("/ban 某人"), CancellationToken.None);

        Assert.True(hit);
        Assert.Empty(plugin.Invocations);
    }

    /// <summary>授予权限后命令正常执行。</summary>
    [Fact]
    public async Task 授予权限后命令正常执行()
    {
        var plugin = new ProtectedPlugin();
        InMemoryPermissionService permissions = CreateService();
        await permissions.GrantAsync(PermissionSubject.User("u1"), "admin.ban", CancellationToken.None);
        CommandDispatcher dispatcher = CreateDispatcher(plugin, permissions);

        var hit = await dispatcher.TryDispatchAsync(CreateMessageEvent("/ban 某人"), CancellationToken.None);

        Assert.True(hit);
        Assert.Equal(["某人"], plugin.Invocations);
    }

    /// <summary>群级授权同样能解锁命令。</summary>
    [Fact]
    public async Task 群级授权能解锁命令()
    {
        var plugin = new ProtectedPlugin();
        InMemoryPermissionService permissions = CreateService();
        await permissions.GrantAsync(PermissionSubject.Group("g1"), "admin.ban", CancellationToken.None);
        CommandDispatcher dispatcher = CreateDispatcher(plugin, permissions);

        await dispatcher.TryDispatchAsync(
            CreateMessageEvent("/ban 某人", conversationType: ConversationType.Group, conversationId: "g1"),
            CancellationToken.None);

        Assert.Equal(["某人"], plugin.Invocations);
    }

    /// <summary>超级管理员无需授权即可执行受保护命令。</summary>
    [Fact]
    public async Task 超级管理员可直接执行受保护命令()
    {
        var plugin = new ProtectedPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(
            plugin,
            CreateService(new PermissionOptions { SuperAdmins = ["root"] }));

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/ban 某人", senderId: "root"), CancellationToken.None);

        Assert.Equal(["某人"], plugin.Invocations);
    }

    /// <summary>别名继承主命令的权限要求。</summary>
    [Fact]
    public async Task 别名继承主命令的权限要求()
    {
        var plugin = new ProtectedPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin, CreateService());

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/封禁 某人"), CancellationToken.None);

        Assert.Empty(plugin.Invocations);
    }

    /// <summary>未声明权限要求的命令不受任何限制（默认放行策略）。</summary>
    [Fact]
    public async Task 未声明要求的命令默认放行()
    {
        var plugin = new OpenPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin, CreateService());

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/hello"), CancellationToken.None);

        Assert.Single(plugin.Invocations);
    }

    /// <summary>权限不足时向用户回复拒绝提示。</summary>
    [Fact]
    public async Task 权限不足时回复拒绝提示()
    {
        var plugin = new ProtectedPlugin();
        var sender = new FakeSender();
        CommandDispatcher dispatcher = CreateDispatcher(plugin, CreateService());

        await dispatcher.TryDispatchAsync(
            CreateMessageEvent("/ban 某人", replyChannel: sender),
            CancellationToken.None);

        Assert.Equal(["你没有权限执行该命令。"], sender.Replies);
    }

    /// <summary>提示语配置为空时静默拒绝（只记日志）。</summary>
    [Fact]
    public async Task 提示语为空时静默拒绝()
    {
        var plugin = new ProtectedPlugin();
        var sender = new FakeSender();
        CommandDispatcher dispatcher = CreateDispatcher(
            plugin,
            CreateService(new PermissionOptions { DeniedMessage = string.Empty }));

        await dispatcher.TryDispatchAsync(
            CreateMessageEvent("/ban 某人", replyChannel: sender),
            CancellationToken.None);

        Assert.Empty(sender.Replies);
    }

    /// <summary>事件没有装配回复通道时，拒绝流程不抛异常。</summary>
    [Fact]
    public async Task 缺少回复通道时不抛异常()
    {
        var plugin = new ProtectedPlugin();
        CommandDispatcher dispatcher = CreateDispatcher(plugin, CreateService());

        var hit = await dispatcher.TryDispatchAsync(
            CreateMessageEvent("/ban 某人", replyChannel: null),
            CancellationToken.None);

        Assert.True(hit);
    }

    /// <summary>多条权限要求按 All 组合：只满足其一仍然被拒绝。</summary>
    [Fact]
    public async Task 多条要求按All组合判定()
    {
        var plugin = new StrictPlugin();
        InMemoryPermissionService permissions = CreateService();
        await permissions.GrantAsync(PermissionSubject.User("u1"), "admin.a", CancellationToken.None);
        CommandDispatcher dispatcher = CreateDispatcher(plugin, permissions);

        await dispatcher.TryDispatchAsync(CreateMessageEvent("/strict"), CancellationToken.None);
        Assert.Empty(plugin.Invocations);

        await permissions.GrantAsync(PermissionSubject.User("u1"), "admin.b", CancellationToken.None);
        await dispatcher.TryDispatchAsync(CreateMessageEvent("/strict"), CancellationToken.None);
        Assert.Single(plugin.Invocations);
    }

    // ---------- 测试基础设施 ----------

    private static CommandDispatcher CreateDispatcher(IPlugin plugin, IPermissionService permissionService)
    {
        var dispatcher = new CommandDispatcher(
            Options.Create(new CommandOptions()),
            permissionService,
            new PluginStateRegistry(),
            NullLogger<CommandDispatcher>.Instance);

        dispatcher.RegisterPluginCommands(plugin);
        return dispatcher;
    }

    private static InMemoryPermissionService CreateService(PermissionOptions? options = null) =>
        new(
            new InMemoryAccessPolicy(Options.Create(options ?? new PermissionOptions())),
            NullLogger<InMemoryPermissionService>.Instance);

    private static MessageEvent CreateMessageEvent(
        string text,
        string senderId = "u1",
        ConversationType conversationType = ConversationType.Private,
        string conversationId = "conv-1",
        IMessageSender? replyChannel = null) => new()
        {
            Conversation = new ConversationId("test", conversationType, conversationId),
            Message = Message.Text(text),
            MessageId = "msg-1",
            SenderId = senderId,
            ReplyChannel = replyChannel,
        };

    /// <summary>带权限要求的插件。</summary>
    private sealed class ProtectedPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        [OnCommand("ban", Aliases = ["封禁"])]
        [RequirePermission("admin.ban")]
        public ValueTask BanAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.ArgsText);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>要求同时具备两个权限节点的插件。</summary>
    private sealed class StrictPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        [OnCommand("strict")]
        [RequirePermission("admin.a", "admin.b")]
        public ValueTask StrictAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.CommandName);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>未声明权限要求的插件。</summary>
    private sealed class OpenPlugin : IPlugin
    {
        public List<string> Invocations { get; } = [];

        [OnCommand("hello")]
        public ValueTask HelloAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(context.CommandName);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>记录回复内容的发送器替身。</summary>
    private sealed class FakeSender : IMessageSender
    {
        public List<string> Replies { get; } = [];

        public ValueTask<SendResult> SendAsync(
            ConversationId conversation,
            Message message,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(SendResult.Ok());

        public ValueTask<SendResult> ReplyAsync(
            ConversationId conversation,
            string replyToMessageId,
            Message message,
            CancellationToken cancellationToken)
        {
            Replies.Add(message.ToString());
            return ValueTask.FromResult(SendResult.Ok());
        }
    }
}
