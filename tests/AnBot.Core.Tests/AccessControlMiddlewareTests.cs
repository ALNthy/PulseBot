using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AnBot.Core.Commands;
using AnBot.Core.Dispatching;
using AnBot.Core.Events;
using AnBot.Core.Messages;
using AnBot.Core.Permissions;

namespace AnBot.Core.Tests;

/// <summary>
/// 接入控制中间件测试：黑/白名单的拦截与放行规则，以及它在管道中的位置。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccessControlMiddlewareTests
{
    /// <summary>黑名单内的用户被拦截，消息不会到达下游。</summary>
    [Fact]
    public async Task 用户黑名单拦截消息()
    {
        Assert.True(await IsBlockedAsync(
            new PermissionOptions { UserBlacklist = ["blocked"] },
            CreateMessageEvent("blocked")));
    }

    /// <summary>黑名单内的群被拦截。</summary>
    [Fact]
    public async Task 群黑名单拦截消息()
    {
        Assert.True(await IsBlockedAsync(
            new PermissionOptions { GroupBlacklist = ["bad-group"] },
            CreateMessageEvent("u1", ConversationType.Group, "bad-group")));
    }

    /// <summary>群黑名单不影响私聊（私聊没有群身份）。</summary>
    [Fact]
    public async Task 群黑名单不影响私聊()
    {
        Assert.False(await IsBlockedAsync(
            new PermissionOptions { GroupBlacklist = ["bad-group"] },
            CreateMessageEvent("u1", ConversationType.Private, "bad-group")));
    }

    /// <summary>白名单为空表示不限制，任何人都能通过。</summary>
    [Fact]
    public async Task 白名单为空时不限制()
    {
        Assert.False(await IsBlockedAsync(new PermissionOptions(), CreateMessageEvent("anyone")));
    }

    /// <summary>用户白名单非空时构成允许列表：名单外的人被拦截，名单内的人放行。</summary>
    [Fact]
    public async Task 用户白名单非空时只允许名单内用户()
    {
        var options = new PermissionOptions { UserWhitelist = ["allowed"] };

        Assert.True(await IsBlockedAsync(options, CreateMessageEvent("outsider")));
        Assert.False(await IsBlockedAsync(options, CreateMessageEvent("allowed")));
    }

    /// <summary>群白名单非空时只允许名单内的群，且不影响私聊。</summary>
    [Fact]
    public async Task 群白名单只对群会话生效()
    {
        var options = new PermissionOptions { GroupWhitelist = ["good-group"] };

        Assert.True(await IsBlockedAsync(options, CreateMessageEvent("u1", ConversationType.Group, "other-group")));
        Assert.False(await IsBlockedAsync(options, CreateMessageEvent("u1", ConversationType.Group, "good-group")));

        // 私聊不受群白名单约束
        Assert.False(await IsBlockedAsync(options, CreateMessageEvent("u1", ConversationType.Private, "other-group")));
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

        Assert.False(await IsBlockedAsync(options, CreateMessageEvent("root")));
        Assert.False(await IsBlockedAsync(options, CreateMessageEvent("root", ConversationType.Group, "other-group")));
    }

    /// <summary>超级管理员仍然受黑名单约束：黑名单是「不能用」，与权限无关。</summary>
    [Fact]
    public async Task 超级管理员仍受黑名单约束()
    {
        Assert.True(await IsBlockedAsync(
            new PermissionOptions { SuperAdmins = ["root"], UserBlacklist = ["root"] },
            CreateMessageEvent("root")));
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

        Assert.True(await IsBlockedAsync(options, CreateMessageEvent("u1")));
    }

    /// <summary>接入控制的优先级必须严格高于命令分发中间件，否则被拉黑的人仍能触发命令。</summary>
    [Fact]
    public void 优先级高于命令分发中间件()
    {
        var commandMiddleware = new CommandMiddleware(new NoopCommandDispatcher());

        Assert.True(CreateMiddleware(new PermissionOptions()).Priority > commandMiddleware.Priority);
    }

    // ---------- 测试基础设施 ----------

    /// <summary>
    /// 把一条消息送进「接入控制 → 下游」的最小管道，返回它是否被拦下。
    /// </summary>
    /// <remarks>
    /// 中间件的拦截语义是「不调用续延」，因此用「下游是否被调用」来观测，而不是
    /// <see cref="EventContext.IsPropagationStopped"/>——后者是处理器之间的机制，
    /// 接入控制迁移到中间件层后已不再使用它。
    /// </remarks>
    private static async Task<bool> IsBlockedAsync(PermissionOptions options, MessageEvent messageEvent)
    {
        var reachedDownstream = false;

        await CreateMiddleware(options).InvokeAsync(
            messageEvent,
            new EventContext(),
            (_, _, _) =>
            {
                reachedDownstream = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        return !reachedDownstream;
    }

    private static AccessControlMiddleware CreateMiddleware(PermissionOptions options) =>
        new(
            new InMemoryAccessPolicy(Options.Create(options)),
            NullLogger<AccessControlMiddleware>.Instance);

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
