using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Commands;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;

namespace PulseBot.Core.Tests;

/// <summary>
/// 接入控制测试：黑/白名单的拦截与放行规则，以及它在管道中的优先级。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccessControlHandlerTests
{
    /// <summary>黑名单内的用户被拦截并阻断传播。</summary>
    [Fact]
    public async Task 用户黑名单拦截消息()
    {
        var context = await RunAsync(
            new PermissionOptions { UserBlacklist = ["blocked"] },
            CreateMessageEvent("blocked"));

        Assert.True(context.IsPropagationStopped);
    }

    /// <summary>黑名单内的群被拦截。</summary>
    [Fact]
    public async Task 群黑名单拦截消息()
    {
        var context = await RunAsync(
            new PermissionOptions { GroupBlacklist = ["bad-group"] },
            CreateMessageEvent("u1", ConversationType.Group, "bad-group"));

        Assert.True(context.IsPropagationStopped);
    }

    /// <summary>群黑名单不影响私聊（私聊没有群身份）。</summary>
    [Fact]
    public async Task 群黑名单不影响私聊()
    {
        var context = await RunAsync(
            new PermissionOptions { GroupBlacklist = ["bad-group"] },
            CreateMessageEvent("u1", ConversationType.Private, "bad-group"));

        Assert.False(context.IsPropagationStopped);
    }

    /// <summary>白名单为空表示不限制，任何人都能通过。</summary>
    [Fact]
    public async Task 白名单为空时不限制()
    {
        var context = await RunAsync(new PermissionOptions(), CreateMessageEvent("anyone"));

        Assert.False(context.IsPropagationStopped);
    }

    /// <summary>用户白名单非空时构成允许列表：名单外的人被拦截，名单内的人放行。</summary>
    [Fact]
    public async Task 用户白名单非空时只允许名单内用户()
    {
        var options = new PermissionOptions { UserWhitelist = ["allowed"] };

        Assert.True((await RunAsync(options, CreateMessageEvent("outsider"))).IsPropagationStopped);
        Assert.False((await RunAsync(options, CreateMessageEvent("allowed"))).IsPropagationStopped);
    }

    /// <summary>群白名单非空时只允许名单内的群，且不影响私聊。</summary>
    [Fact]
    public async Task 群白名单只对群会话生效()
    {
        var options = new PermissionOptions { GroupWhitelist = ["good-group"] };

        Assert.True((await RunAsync(options, CreateMessageEvent("u1", ConversationType.Group, "other-group")))
            .IsPropagationStopped);
        Assert.False((await RunAsync(options, CreateMessageEvent("u1", ConversationType.Group, "good-group")))
            .IsPropagationStopped);

        // 私聊不受群白名单约束
        Assert.False((await RunAsync(options, CreateMessageEvent("u1", ConversationType.Private, "other-group")))
            .IsPropagationStopped);
    }

    /// <summary>超级管理员不受白名单限制，避免把自己锁在门外。</summary>
    [Fact]
    public async Task 超级管理员不受白名单限制()
    {
        var options = new PermissionOptions
        {
            SuperAdmins = ["root"],
            UserWhitelist = ["someone-else"],
            GroupWhitelist = ["some-group"],
        };

        Assert.False((await RunAsync(options, CreateMessageEvent("root"))).IsPropagationStopped);
        Assert.False((await RunAsync(options, CreateMessageEvent("root", ConversationType.Group, "other-group")))
            .IsPropagationStopped);
    }

    /// <summary>超级管理员仍然受黑名单约束：黑名单是「不能用」，与权限无关。</summary>
    [Fact]
    public async Task 超级管理员仍受黑名单约束()
    {
        var context = await RunAsync(
            new PermissionOptions { SuperAdmins = ["root"], UserBlacklist = ["root"] },
            CreateMessageEvent("root"));

        Assert.True(context.IsPropagationStopped);
    }

    /// <summary>同时命中黑名单与白名单时，黑名单优先。</summary>
    [Fact]
    public async Task 黑名单优先于白名单()
    {
        var options = new PermissionOptions
        {
            UserBlacklist = ["u1"],
            UserWhitelist = ["u1"],
        };

        Assert.True((await RunAsync(options, CreateMessageEvent("u1"))).IsPropagationStopped);
    }

    /// <summary>接入控制的优先级必须严格高于命令分发处理器，否则被拉黑的人仍能触发命令。</summary>
    [Fact]
    public void 优先级高于命令分发处理器()
    {
        var commandHandler = new CommandMessageHandler(new NoopCommandDispatcher());

        Assert.True(CreateHandler(new PermissionOptions()).Priority > commandHandler.Priority);
    }

    // ---------- 测试基础设施 ----------

    private static async Task<EventContext> RunAsync(PermissionOptions options, MessageEvent messageEvent)
    {
        var context = new EventContext();
        await CreateHandler(options).HandleAsync(messageEvent, context, CancellationToken.None);
        return context;
    }

    private static AccessControlHandler CreateHandler(PermissionOptions options) =>
        new(Options.Create(options), NullLogger<AccessControlHandler>.Instance);

    private static MessageEvent CreateMessageEvent(
        string senderId,
        ConversationType type = ConversationType.Private,
        string conversationId = "conv-1") => new()
        {
            Conversation = new ConversationId("test", type, conversationId),
            Message = Message.Text("测试"),
            MessageId = "msg-1",
            SenderId = senderId,
        };

    /// <summary>仅用于比较优先级的空命令分发器。</summary>
    private sealed class NoopCommandDispatcher : ICommandDispatcher
    {
        public IReadOnlyCollection<CommandDescriptor> Commands => [];

        public ValueTask<bool> TryDispatchAsync(MessageEvent messageEvent, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }
}
