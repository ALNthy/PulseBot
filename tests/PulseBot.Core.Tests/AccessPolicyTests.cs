using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;

namespace PulseBot.Core.Tests;

/// <summary>
/// 接入策略热更新测试：快照被整体替换后，接入控制与权限判定都必须立即改用新名单，
/// 既不需要重启宿主，也不需要重建任何消费方实例。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccessPolicyTests
{
    /// <summary>未显式更新时，策略来自配置播种——不接后台管理时行为与过去一致。</summary>
    [Fact]
    public async Task 未更新时使用配置播种的名单()
    {
        InMemoryAccessPolicy policy = CreatePolicy(new PermissionOptions { UserBlacklist = ["blocked"] });

        Assert.True(await IsBlockedAsync(CreateMiddleware(policy), CreateMessageEvent("blocked")));
    }

    /// <summary>更新快照后，同一条接入控制中间件实例立即按新黑名单判定。</summary>
    [Fact]
    public async Task 更新黑名单后立即生效()
    {
        InMemoryAccessPolicy policy = CreatePolicy();
        AccessControlMiddleware middleware = CreateMiddleware(policy);

        // 更新前无名单，消息照常放行。
        Assert.False(await IsBlockedAsync(middleware, CreateMessageEvent("u1")));

        policy.Update(new AccessPolicySnapshot([], ["u1"], [], [], []));

        Assert.True(await IsBlockedAsync(middleware, CreateMessageEvent("u1")));
    }

    /// <summary>热更新是双向的：把用户移出黑名单后立即恢复放行。</summary>
    [Fact]
    public async Task 移除黑名单后立即恢复放行()
    {
        InMemoryAccessPolicy policy = CreatePolicy(new PermissionOptions { UserBlacklist = ["u1"] });
        AccessControlMiddleware middleware = CreateMiddleware(policy);

        Assert.True(await IsBlockedAsync(middleware, CreateMessageEvent("u1")));

        policy.Update(new AccessPolicySnapshot([], [], [], [], []));

        Assert.False(await IsBlockedAsync(middleware, CreateMessageEvent("u1")));
    }

    /// <summary>超管名单热更新后立即影响权限判定：新超管无需逐条授权即拥有全部权限。</summary>
    [Fact]
    public async Task 更新超管名单后权限判定立即生效()
    {
        InMemoryAccessPolicy policy = CreatePolicy();
        var permissions = new InMemoryPermissionService(policy, NullLogger<InMemoryPermissionService>.Instance);
        var requirement = new PermissionRequirement(PermissionRequirementMode.All, ["admin.ban"]);

        Assert.False(await permissions.IsGrantedAsync(
            CreateMessageEvent("u1"), requirement, CancellationToken.None));

        policy.Update(new AccessPolicySnapshot(["u1"], [], [], [], []));

        Assert.True(await permissions.IsGrantedAsync(
            CreateMessageEvent("u1"), requirement, CancellationToken.None));
    }

    /// <summary>权限不足提示语同样取自当前快照，更新后立即生效。</summary>
    [Fact]
    public void 更新提示语后立即生效()
    {
        InMemoryAccessPolicy policy = CreatePolicy(new PermissionOptions { DeniedMessage = "旧提示" });
        var permissions = new InMemoryPermissionService(policy, NullLogger<InMemoryPermissionService>.Instance);

        Assert.Equal("旧提示", permissions.DeniedMessage);

        policy.Update(new AccessPolicySnapshot([], [], [], [], [], "新提示"));

        Assert.Equal("新提示", permissions.DeniedMessage);
    }

    /// <summary>
    /// 名单归一化：空串与纯空白项被剔除。
    /// 若放任空串留在白名单里，「白名单非空 = 允许列表」就会生效却匹配不到任何人，
    /// 等于把所有人关在门外。
    /// </summary>
    [Fact]
    public async Task 白名单中的空白项被剔除()
    {
        InMemoryAccessPolicy policy = CreatePolicy();
        AccessControlMiddleware middleware = CreateMiddleware(policy);

        policy.Update(new AccessPolicySnapshot([], [], ["   ", ""], [], []));

        Assert.Empty(policy.Current.UserWhitelist);

        // 白名单因此仍为空 = 不限制，普通用户照常放行。
        Assert.False(await IsBlockedAsync(middleware, CreateMessageEvent("u1")));
    }

    /// <summary>名单项的首尾空白被裁剪，避免后台表单里的空格导致名单形同虚设。</summary>
    [Fact]
    public void 名单项首尾空白被裁剪()
    {
        InMemoryAccessPolicy policy = CreatePolicy();

        policy.Update(new AccessPolicySnapshot([" 10001 "], [], [], [], []));

        // 断言整个集合：既证明带空格的原值没被收下，也证明裁剪后的值确实在。
        Assert.Equal(["10001"], policy.Current.SuperAdmins);
    }

    /// <summary>提示语为 <c>null</c> 时落到空串（静默拒绝），而不是把 null 传播到回复逻辑。</summary>
    [Fact]
    public void 提示语为null时视为静默拒绝()
    {
        InMemoryAccessPolicy policy = CreatePolicy();

        policy.Update(new AccessPolicySnapshot([], [], [], [], [], deniedMessage: null));

        Assert.Equal(string.Empty, policy.Current.DeniedMessage);
    }

    // ---------- 测试基础设施 ----------

    private static InMemoryAccessPolicy CreatePolicy(PermissionOptions? options = null)
        => new(Options.Create(options ?? new PermissionOptions()));

    private static AccessControlMiddleware CreateMiddleware(IAccessPolicy policy) =>
        new(policy, NullLogger<AccessControlMiddleware>.Instance);

    /// <summary>跑一遍中间件，返回消息是否被拦下（下游未被调用即为拦截）。</summary>
    private static async Task<bool> IsBlockedAsync(AccessControlMiddleware middleware, MessageEvent messageEvent)
    {
        var reachedDownstream = false;

        await middleware.InvokeAsync(
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

    private static MessageEvent CreateMessageEvent(string senderId) => new()
    {
        Conversation = new ConversationId("test", ConversationType.Private, "conv-1"),
        Message = Message.Text("测试"),
        MessageId = "msg-1",
        SenderId = senderId,
    };
}
