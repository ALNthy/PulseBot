using PulseBot.Admin.Infrastructure.Control;

namespace PulseBot.Admin.Tests;

/// <summary>
/// 运行时控制状态的持久化测试：权限名单与插件启停的读写语义。
/// </summary>
/// <remarks>
/// 用真实的内存 SQLite：这两张表的价值全在「跨重启保留」，
/// 用假仓储测不出映射、主键与 JSON 列转换上的问题。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class RuntimeControlStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>写入的名单能原样读回。</summary>
    [Fact]
    public async Task 名单写入后可读回()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PermissionListStore store = NewListStore(database, new MutableTimeProvider(Start));

        await store.UpsertAsync(
            PermissionListKeys.UserBlacklist,
            ["10001", "10002"],
            CancellationToken.None);

        IReadOnlyDictionary<string, string[]> lists = await store.GetAllAsync(CancellationToken.None);

        Assert.Equal(["10001", "10002"], lists[PermissionListKeys.UserBlacklist]);
    }

    /// <summary>库里没有的名单不出现在结果里——调用方据此判断哪几份需要按配置播种。</summary>
    [Fact]
    public async Task 未持久化的名单不出现在结果里()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PermissionListStore store = NewListStore(database, new MutableTimeProvider(Start));

        Assert.Empty(await store.GetAllAsync(CancellationToken.None));
    }

    /// <summary>重复写入同一份名单是覆盖而不是追加，行数也不增长。</summary>
    [Fact]
    public async Task 重复写入是覆盖()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PermissionListStore store = NewListStore(database, new MutableTimeProvider(Start));

        await store.UpsertAsync(PermissionListKeys.SuperAdmins, ["old"], CancellationToken.None);
        await store.UpsertAsync(PermissionListKeys.SuperAdmins, ["new-1", "new-2"], CancellationToken.None);

        IReadOnlyDictionary<string, string[]> lists = await store.GetAllAsync(CancellationToken.None);

        Assert.Single(lists);
        Assert.Equal(["new-1", "new-2"], lists[PermissionListKeys.SuperAdmins]);
    }

    /// <summary>各份名单互不影响：更新一份不会动到另一份。</summary>
    [Fact]
    public async Task 各份名单互不影响()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PermissionListStore store = NewListStore(database, new MutableTimeProvider(Start));

        await store.UpsertAsync(PermissionListKeys.SuperAdmins, ["1"], CancellationToken.None);
        await store.UpsertAsync(PermissionListKeys.UserBlacklist, ["2"], CancellationToken.None);
        await store.UpsertAsync(PermissionListKeys.UserBlacklist, ["3"], CancellationToken.None);

        IReadOnlyDictionary<string, string[]> lists = await store.GetAllAsync(CancellationToken.None);

        Assert.Equal(["1"], lists[PermissionListKeys.SuperAdmins]);
        Assert.Equal(["3"], lists[PermissionListKeys.UserBlacklist]);
    }

    /// <summary>
    /// 写入空数组表示「这份名单是空的」，与「从未配置过」是两种状态：
    /// 前者不再被配置播种，后者会被播种。这个区分是名单能否稳定覆盖配置的前提。
    /// </summary>
    [Fact]
    public async Task 空名单与未配置可区分()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PermissionListStore store = NewListStore(database, new MutableTimeProvider(Start));

        await store.UpsertAsync(PermissionListKeys.GroupBlacklist, [], CancellationToken.None);

        IReadOnlyDictionary<string, string[]> lists = await store.GetAllAsync(CancellationToken.None);

        Assert.True(lists.ContainsKey(PermissionListKeys.GroupBlacklist));
        Assert.Empty(lists[PermissionListKeys.GroupBlacklist]);
    }

    /// <summary>标识集合覆盖五份名单，并能识别未知标识（接口层据此回 400）。</summary>
    [Fact]
    public void 标识集合覆盖五份名单()
    {
        Assert.Equal(5, PermissionListKeys.All.Count);
        Assert.True(PermissionListKeys.IsKnown(PermissionListKeys.SuperAdmins));
        Assert.True(PermissionListKeys.IsKnown(PermissionListKeys.GroupWhitelist));
        Assert.False(PermissionListKeys.IsKnown("no-such-list"));
    }

    /// <summary>默认没有任何插件被停用。</summary>
    [Fact]
    public async Task 默认没有插件被停用()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PluginStateStore store = NewStateStore(database, new MutableTimeProvider(Start));

        Assert.Empty(await store.GetDisabledAsync(CancellationToken.None));
    }

    /// <summary>停用后该插件出现在停用列表里。</summary>
    [Fact]
    public async Task 停用后进入停用列表()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PluginStateStore store = NewStateStore(database, new MutableTimeProvider(Start));

        await store.SetEnabledAsync("Demo.Plugin", enabled: false, CancellationToken.None);

        Assert.Equal(["Demo.Plugin"], await store.GetDisabledAsync(CancellationToken.None));
    }

    /// <summary>启用即删除记录，插件移出停用列表。</summary>
    [Fact]
    public async Task 启用后移出停用列表()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PluginStateStore store = NewStateStore(database, new MutableTimeProvider(Start));

        await store.SetEnabledAsync("Demo.Plugin", enabled: false, CancellationToken.None);
        await store.SetEnabledAsync("Demo.Plugin", enabled: true, CancellationToken.None);

        Assert.Empty(await store.GetDisabledAsync(CancellationToken.None));
    }

    /// <summary>重复停用与重复启用都是幂等的（后台可以重放同一请求）。</summary>
    [Fact]
    public async Task 重复设置是幂等的()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PluginStateStore store = NewStateStore(database, new MutableTimeProvider(Start));

        await store.SetEnabledAsync("Demo.Plugin", enabled: false, CancellationToken.None);
        await store.SetEnabledAsync("Demo.Plugin", enabled: false, CancellationToken.None);
        Assert.Single(await store.GetDisabledAsync(CancellationToken.None));

        await store.SetEnabledAsync("Demo.Plugin", enabled: true, CancellationToken.None);
        await store.SetEnabledAsync("Demo.Plugin", enabled: true, CancellationToken.None);
        Assert.Empty(await store.GetDisabledAsync(CancellationToken.None));
    }

    /// <summary>多个插件的停用状态各自独立。</summary>
    [Fact]
    public async Task 多个插件的状态互不影响()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        PluginStateStore store = NewStateStore(database, new MutableTimeProvider(Start));

        await store.SetEnabledAsync("A.Plugin", enabled: false, CancellationToken.None);
        await store.SetEnabledAsync("B.Plugin", enabled: false, CancellationToken.None);
        await store.SetEnabledAsync("A.Plugin", enabled: true, CancellationToken.None);

        Assert.Equal(["B.Plugin"], await store.GetDisabledAsync(CancellationToken.None));
    }

    // ---------- 测试基础设施 ----------

    private static PermissionListStore NewListStore(AdminTestDatabase database, MutableTimeProvider clock)
        => new(database.Factory, clock);

    private static PluginStateStore NewStateStore(AdminTestDatabase database, MutableTimeProvider clock)
        => new(database.Factory, clock);
}
