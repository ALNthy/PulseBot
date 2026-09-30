using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;

namespace PulseBot.Core.Tests;

/// <summary>
/// 权限判定测试：三级主体合并、超级管理员、通配节点、All/Any 组合，以及授予/撤销。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PermissionServiceTests
{
    /// <summary>未声明任何权限要求时一律放行（默认放行策略）。</summary>
    [Fact]
    public async Task 空要求一律放行()
    {
        InMemoryPermissionService service = CreateService();

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("u1"),
            PermissionRequirement.None,
            CancellationToken.None));
    }

    /// <summary>未授予权限时判定为拒绝。</summary>
    [Fact]
    public async Task 未授予权限时拒绝()
    {
        InMemoryPermissionService service = CreateService();

        Assert.False(await service.IsGrantedAsync(
            CreateMessageEvent("u1"),
            new PermissionRequirement(PermissionRequirementMode.All, ["admin.ban"]),
            CancellationToken.None));
    }

    /// <summary>用户级授权在该用户出现的任意会话中生效。</summary>
    [Fact]
    public async Task 用户级授权在群聊与私聊中都生效()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(PermissionSubject.User("u1"), "admin.ban", CancellationToken.None);
        var requirement = new PermissionRequirement(PermissionRequirementMode.All, ["admin.ban"]);

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("u1", ConversationType.Group, "g1"), requirement, CancellationToken.None));
        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("u1", ConversationType.Private, "u1"), requirement, CancellationToken.None));
    }

    /// <summary>群级授权只在该群会话中生效，私聊没有群身份因而无效。</summary>
    [Fact]
    public async Task 群级授权只在群会话中生效()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(PermissionSubject.Group("g1"), "admin.ban", CancellationToken.None);
        var requirement = new PermissionRequirement(PermissionRequirementMode.All, ["admin.ban"]);

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("u1", ConversationType.Group, "g1"), requirement, CancellationToken.None));

        // 同一用户在私聊里得不到该群的授权
        Assert.False(await service.IsGrantedAsync(
            CreateMessageEvent("u1", ConversationType.Private, "u1"), requirement, CancellationToken.None));

        // 别的群也不行
        Assert.False(await service.IsGrantedAsync(
            CreateMessageEvent("u1", ConversationType.Group, "g2"), requirement, CancellationToken.None));
    }

    /// <summary>全局授权对所有会话的所有用户生效。</summary>
    [Fact]
    public async Task 全局授权对所有人生效()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(PermissionSubject.Global, "weather.query", CancellationToken.None);
        var requirement = new PermissionRequirement(PermissionRequirementMode.All, ["weather.query"]);

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("anyone", ConversationType.Group, "g1"), requirement, CancellationToken.None));
        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("other", ConversationType.Private, "other"), requirement, CancellationToken.None));
    }

    /// <summary>超级管理员无需逐条授予即拥有全部权限。</summary>
    [Fact]
    public async Task 超级管理员拥有全部权限()
    {
        InMemoryPermissionService service = CreateService(new PermissionOptions { SuperAdmins = ["root"] });

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("root"),
            new PermissionRequirement(PermissionRequirementMode.All, ["anything.at.all"]),
            CancellationToken.None));
    }

    /// <summary>通配节点 * 等价于拥有全部权限。</summary>
    [Fact]
    public async Task 通配节点等价于全部权限()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(
            PermissionSubject.User("u1"),
            InMemoryPermissionService.WildcardNode,
            CancellationToken.None);

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("u1"),
            new PermissionRequirement(PermissionRequirementMode.All, ["a.b", "c.d"]),
            CancellationToken.None));
    }

    /// <summary>All 模式要求每个节点都被满足，缺一即拒绝。</summary>
    [Fact]
    public async Task All模式要求全部节点满足()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(PermissionSubject.User("u1"), "a", CancellationToken.None);
        var requirement = new PermissionRequirement(PermissionRequirementMode.All, ["a", "b"]);

        Assert.False(await service.IsGrantedAsync(CreateMessageEvent("u1"), requirement, CancellationToken.None));

        await service.GrantAsync(PermissionSubject.User("u1"), "b", CancellationToken.None);

        Assert.True(await service.IsGrantedAsync(CreateMessageEvent("u1"), requirement, CancellationToken.None));
    }

    /// <summary>Any 模式满足任一节点即通过。</summary>
    [Fact]
    public async Task Any模式满足任一节点即可()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(PermissionSubject.User("u1"), "b", CancellationToken.None);
        var requirement = new PermissionRequirement(PermissionRequirementMode.Any, ["a", "b"]);

        Assert.True(await service.IsGrantedAsync(CreateMessageEvent("u1"), requirement, CancellationToken.None));
    }

    /// <summary>不同主体的授权可以叠加：用户有 a、群有 b，则「a 且 b」在群会话中成立。</summary>
    [Fact]
    public async Task 不同层级的授权可以叠加()
    {
        InMemoryPermissionService service = CreateService();
        await service.GrantAsync(PermissionSubject.User("u1"), "a", CancellationToken.None);
        await service.GrantAsync(PermissionSubject.Group("g1"), "b", CancellationToken.None);
        var requirement = new PermissionRequirement(PermissionRequirementMode.All, ["a", "b"]);

        Assert.True(await service.IsGrantedAsync(
            CreateMessageEvent("u1", ConversationType.Group, "g1"), requirement, CancellationToken.None));
    }

    /// <summary>撤销后立刻失效；重复撤销与撤销不存在的授权都是幂等的。</summary>
    [Fact]
    public async Task 撤销授权后判定失效()
    {
        InMemoryPermissionService service = CreateService();
        var subject = PermissionSubject.User("u1");
        await service.GrantAsync(subject, "admin.ban", CancellationToken.None);

        await service.RevokeAsync(subject, "admin.ban", CancellationToken.None);
        await service.RevokeAsync(subject, "admin.ban", CancellationToken.None);
        await service.RevokeAsync(subject, "never.granted", CancellationToken.None);

        Assert.False(await service.IsGrantedAsync(subject, "admin.ban", CancellationToken.None));
    }

    /// <summary>撤销只影响指定节点，同主体的其它授权保持不变。</summary>
    [Fact]
    public async Task 撤销只移除指定节点()
    {
        InMemoryPermissionService service = CreateService();
        var subject = PermissionSubject.User("u1");
        await service.GrantAsync(subject, "a", CancellationToken.None);
        await service.GrantAsync(subject, "b", CancellationToken.None);

        await service.RevokeAsync(subject, "a", CancellationToken.None);

        Assert.False(await service.IsGrantedAsync(subject, "a", CancellationToken.None));
        Assert.True(await service.IsGrantedAsync(subject, "b", CancellationToken.None));
    }

    /// <summary>重复授予同一节点是幂等的。</summary>
    [Fact]
    public async Task 重复授予是幂等的()
    {
        InMemoryPermissionService service = CreateService();
        var subject = PermissionSubject.User("u1");

        await service.GrantAsync(subject, "a", CancellationToken.None);
        await service.GrantAsync(subject, "a", CancellationToken.None);

        var nodes = await service.ListAsync(subject, CancellationToken.None);

        Assert.Equal(["a"], nodes);
    }

    /// <summary>列出主体当前拥有的权限节点；未授予任何权限时返回空集合。</summary>
    [Fact]
    public async Task 列出主体已授予的权限节点()
    {
        InMemoryPermissionService service = CreateService();
        var subject = PermissionSubject.Group("g1");
        await service.GrantAsync(subject, "a", CancellationToken.None);
        await service.GrantAsync(subject, "b", CancellationToken.None);

        var nodes = await service.ListAsync(subject, CancellationToken.None);

        Assert.Equal(["a", "b"], nodes.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(await service.ListAsync(PermissionSubject.User("nobody"), CancellationToken.None));
    }

    /// <summary>直接按主体查询时，超级管理员同样拥有全部权限。</summary>
    [Fact]
    public async Task 按主体查询时超级管理员仍拥有全部权限()
    {
        InMemoryPermissionService service = CreateService(new PermissionOptions { SuperAdmins = ["root"] });

        Assert.True(await service.IsGrantedAsync(PermissionSubject.User("root"), "x", CancellationToken.None));
        Assert.False(await service.IsGrantedAsync(PermissionSubject.User("other"), "x", CancellationToken.None));
    }

    /// <summary>拒绝提示文本来自配置，空字符串表示静默拒绝。</summary>
    [Fact]
    public void 拒绝提示文本来自配置()
    {
        Assert.Equal("你没有权限执行该命令。", CreateService().DeniedMessage);
        Assert.Equal(string.Empty, CreateService(new PermissionOptions { DeniedMessage = string.Empty }).DeniedMessage);
    }

    /// <summary>非法参数在入口处被拒绝。</summary>
    [Fact]
    public async Task 非法参数被拒绝()
    {
        InMemoryPermissionService service = CreateService();

        Assert.Throws<ArgumentException>(() => PermissionSubject.User(" "));
        Assert.Throws<ArgumentException>(() => PermissionSubject.Group(string.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await service.GrantAsync(null!, "a", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.GrantAsync(PermissionSubject.User("u1"), " ", CancellationToken.None));
    }

    /// <summary>含空白字符的权限节点在构造要求时即被拒绝。</summary>
    [Fact]
    public void 含空白的权限节点被拒绝()
    {
        Assert.Throws<ArgumentException>(
            () => new PermissionRequirement(PermissionRequirementMode.All, ["has space"]));
        Assert.Throws<ArgumentException>(
            () => new RequirePermissionAttribute("has space"));
        Assert.Throws<ArgumentException>(
            () => new RequirePermissionAttribute());
    }

    // ---------- 测试基础设施 ----------

    private static InMemoryPermissionService CreateService(PermissionOptions? options = null) =>
        new(Options.Create(options ?? new PermissionOptions()), NullLogger<InMemoryPermissionService>.Instance);

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
}
