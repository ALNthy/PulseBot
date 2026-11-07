using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Infrastructure.Control;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;
using AnBot.Admin.Shared.Accounts;
using AnBot.Admin.Shared.Control;
using AnBot.Core.Commands;
using AnBot.Core.Permissions;
using AnBot.Core.Plugins;

namespace AnBot.Admin.Tests;

/// <summary>
/// 运行时控制链路测试：配置播种、数据库优先、更新立即生效、插件启停的持久化。
/// </summary>
/// <remarks>
/// 用真实的内存 SQLite 与真实的内核持有者（<see cref="InMemoryAccessPolicy"/>、
/// <see cref="PluginStateRegistry"/>、<see cref="PluginManager"/>），因此
/// 「重启后仍是库里的值」这类结论是真的跨了「重建全部内存对象」这一步验证的。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class RuntimeControlServiceTests
{
    private const string RemoteIp = "127.0.0.1";
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly AccountActor Actor = new(UserId: 1, UserName: "admin", RemoteIp: RemoteIp);

    /// <summary>库中没有任何名单时，用配置文件的值播种。</summary>
    [Fact]
    public async Task 首次启动按配置播种()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { SuperAdmins = ["10001"], UserBlacklist = ["20002"] });

        // 内核侧立即生效。
        Assert.Equal(["10001"], harness.AccessPolicy.Current.SuperAdmins);
        Assert.Equal(["20002"], harness.AccessPolicy.Current.UserBlacklist);

        // 并且已经落库，后台打开时能看到它们。
        IReadOnlyDictionary<string, string[]> stored = await harness.Lists.GetAllAsync(CancellationToken.None);
        Assert.Equal(["10001"], stored[PermissionListKeys.SuperAdmins]);
        Assert.Equal(["20002"], stored[PermissionListKeys.UserBlacklist]);

        // 配置里没写的名单也会被播种成空名单：这样后台看到的是「这份名单是空的」，
        // 而不是「这份名单不存在」。
        Assert.Empty(stored[PermissionListKeys.GroupWhitelist]);
    }

    /// <summary>重启后以数据库为准，配置文件里的旧值不再生效。</summary>
    [Fact]
    public async Task 重启后以数据库为准()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { SuperAdmins = ["10001"] });

        await harness.Service.UpdateListAsync(
            Actor,
            PermissionListKeys.SuperAdmins,
            ["99999"],
            CancellationToken.None);

        await harness.RestartAsync();

        Assert.Equal(["99999"], harness.AccessPolicy.Current.SuperAdmins);
    }

    /// <summary>
    /// 把一份名单清空后重启，不会被配置文件重新填满。
    /// </summary>
    /// <remarks>
    /// 这是「库里有空数组」与「库里没有这一行」必须分开表达的原因：
    /// 若把两者混为一谈，管理员在后台删掉的条目会在每次重启后复活。
    /// </remarks>
    [Fact]
    public async Task 清空名单后重启不会被配置填满()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { UserBlacklist = ["20002"] });

        await harness.Service.UpdateListAsync(
            Actor,
            PermissionListKeys.UserBlacklist,
            [],
            CancellationToken.None);

        await harness.RestartAsync();

        Assert.Empty(harness.AccessPolicy.Current.UserBlacklist);
    }

    /// <summary>更新一份名单立即生效，并写入审计。</summary>
    [Fact]
    public async Task 更新名单立即生效并写审计()
    {
        await using var harness = await Harness.CreateAsync();

        RuntimeControlStatus status = await harness.Service.UpdateListAsync(
            Actor,
            PermissionListKeys.GroupWhitelist,
            ["30001", "30002"],
            CancellationToken.None);

        Assert.Equal(RuntimeControlStatus.Success, status);
        Assert.Equal(["30001", "30002"], harness.AccessPolicy.Current.GroupWhitelist);

        AuditLog log = Assert.Single(await harness.ListAuditAsync());
        Assert.Equal("control.update-list", log.Action);
        Assert.Equal("admin", log.ActorName);
        Assert.Equal(PermissionListKeys.GroupWhitelist, log.TargetId);
    }

    /// <summary>更新一份名单不会动到其它名单。</summary>
    [Fact]
    public async Task 更新一份名单不影响其它名单()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { SuperAdmins = ["10001"], UserBlacklist = ["20002"] });

        await harness.Service.UpdateListAsync(
            Actor,
            PermissionListKeys.UserBlacklist,
            ["30003"],
            CancellationToken.None);

        Assert.Equal(["10001"], harness.AccessPolicy.Current.SuperAdmins);
        Assert.Equal(["30003"], harness.AccessPolicy.Current.UserBlacklist);
    }

    /// <summary>未知的名单标识被拒绝，且不留下任何痕迹。</summary>
    [Fact]
    public async Task 更新未知名单被拒绝()
    {
        await using var harness = await Harness.CreateAsync();

        RuntimeControlStatus status = await harness.Service.UpdateListAsync(
            Actor,
            "no-such-list",
            ["1"],
            CancellationToken.None);

        Assert.Equal(RuntimeControlStatus.UnknownList, status);
        Assert.Empty(await harness.ListAuditAsync());
    }

    /// <summary>接口读取的是当前生效的快照，与内核判定用的是同一份数据。</summary>
    [Fact]
    public async Task 读取的名单与生效的一致()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { DeniedMessage = "无权。" });

        await harness.Service.UpdateListAsync(
            Actor,
            PermissionListKeys.SuperAdmins,
            ["10001"],
            CancellationToken.None);

        AccessPolicyInfo info = await harness.Service.GetAccessPolicyAsync(CancellationToken.None);

        // 读取接口带回的标识必须能直接用于写接口——这正是把标识放进列表而非
        // 隐含在属性名里的原因，这条断言守住两者不会各改各的。
        PermissionListInfo superAdmins = Assert.Single(
            info.Lists, list => list.Key == PermissionListKeys.SuperAdmins);
        Assert.Equal(["10001"], superAdmins.Values);
        Assert.Equal("无权。", info.DeniedMessage);
    }

    /// <summary>停用插件立即生效，并落库。</summary>
    [Fact]
    public async Task 停用插件立即生效并落库()
    {
        var plugin = new DemoPlugin();
        await using var harness = await Harness.CreateAsync(plugins: [plugin]);
        string typeName = TypeNameOf(plugin);

        RuntimeControlStatus status = await harness.Service
            .SetPluginEnabledAsync(Actor, typeName, enabled: false, CancellationToken.None);

        Assert.Equal(RuntimeControlStatus.Success, status);
        Assert.True(harness.PluginState.IsDisabled(typeName));
        Assert.False(harness.Plugins.IsEnabled(typeName));
        Assert.Equal([typeName], await harness.PluginStore.GetDisabledAsync(CancellationToken.None));

        AuditLog log = Assert.Single(await harness.ListAuditAsync());
        Assert.Equal("control.set-plugin-state", log.Action);
        Assert.Equal(typeName, log.TargetId);
    }

    /// <summary>停用状态跨重启保持——这正是它必须落库的原因。</summary>
    [Fact]
    public async Task 停用状态在重启后保持()
    {
        var plugin = new DemoPlugin();
        await using var harness = await Harness.CreateAsync(plugins: [plugin]);
        string typeName = TypeNameOf(plugin);

        await harness.Service.SetPluginEnabledAsync(Actor, typeName, enabled: false, CancellationToken.None);
        await harness.RestartAsync();

        Assert.True(harness.PluginState.IsDisabled(typeName));

        // 重新启用后重启，停用状态同样被清除。
        await harness.Service.SetPluginEnabledAsync(Actor, typeName, enabled: true, CancellationToken.None);
        await harness.RestartAsync();

        Assert.False(harness.PluginState.IsDisabled(typeName));
    }

    /// <summary>类型名对不上任何已登记插件时拒绝，且不改动状态。</summary>
    [Fact]
    public async Task 停用未知插件被拒绝()
    {
        var plugin = new DemoPlugin();
        await using var harness = await Harness.CreateAsync(plugins: [plugin]);

        RuntimeControlStatus status = await harness.Service
            .SetPluginEnabledAsync(Actor, "No.Such.Plugin", enabled: false, CancellationToken.None);

        Assert.Equal(RuntimeControlStatus.PluginNotFound, status);
        Assert.Empty(harness.PluginState.DisabledPlugins);
        Assert.Empty(await harness.PluginStore.GetDisabledAsync(CancellationToken.None));
        Assert.Empty(await harness.ListAuditAsync());
    }

    /// <summary>后台关闭时跳过装载，名单保持配置文件给出的初值。</summary>
    [Fact]
    public async Task 后台关闭时跳过装载()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { SuperAdmins = ["10001"] });

        // 库里放一份与内存快照不同的名单，模拟「在后台改过之后又把后台关掉」。
        await harness.Lists.UpsertAsync(
            PermissionListKeys.SuperAdmins,
            ["99999"],
            CancellationToken.None);

        await NewInitializer(harness, enabled: false).StartAsync(CancellationToken.None);

        // 装载被跳过：快照仍是内存里那份。
        Assert.Equal(["10001"], harness.AccessPolicy.Current.SuperAdmins);
    }

    /// <summary>后台启用时执行装载——与上一条对照，证明跳过与否确实取决于配置。</summary>
    [Fact]
    public async Task 后台启用时执行装载()
    {
        await using var harness = await Harness.CreateAsync(
            new PermissionOptions { SuperAdmins = ["10001"] });

        await harness.Lists.UpsertAsync(
            PermissionListKeys.SuperAdmins,
            ["99999"],
            CancellationToken.None);

        await NewInitializer(harness, enabled: true).StartAsync(CancellationToken.None);

        Assert.Equal(["99999"], harness.AccessPolicy.Current.SuperAdmins);
    }

    // ---------- 测试基础设施 ----------

    private static RuntimeControlInitializer NewInitializer(Harness harness, bool enabled)
        => new(
            harness.Service,
            Options.Create(new AdminOptions { Enabled = enabled }),
            NullLogger<RuntimeControlInitializer>.Instance);

    /// <summary>
    /// 一套装配好的运行时控制链路，可在同一个数据库上反复「重启」。
    /// </summary>
    /// <remarks>
    /// 重启重建的是全部内存对象（策略快照、插件状态、插件管理器），
    /// 只保留数据库文件——与真实宿主重启时的边界一致。
    /// </remarks>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly PermissionOptions _permissions;
        private readonly IReadOnlyList<IPlugin> _plugins;

        /// <summary>当前这一「代」内存对象；重启时整体换掉。</summary>
        private Components _components;

        private Harness(
            AdminTestDatabase database,
            MutableTimeProvider clock,
            PermissionOptions permissions,
            IReadOnlyList<IPlugin> plugins)
        {
            Database = database;
            Clock = clock;
            _permissions = permissions;
            _plugins = plugins;

            _components = CreateComponents();
        }

        public AdminTestDatabase Database { get; }

        public MutableTimeProvider Clock { get; }

        public RuntimeControlService Service => _components.Service;

        public InMemoryAccessPolicy AccessPolicy => _components.AccessPolicy;

        public PluginStateRegistry PluginState => _components.PluginState;

        public PluginManager Plugins => _components.Plugins;

        public IPermissionListStore Lists => _components.Lists;

        public IPluginStateStore PluginStore => _components.PluginStore;

        /// <summary>建库并完成首次装载。</summary>
        /// <param name="permissions">权限配置（播种来源）。</param>
        /// <param name="plugins">容器中的插件；默认只有一个演示插件。</param>
        public static async Task<Harness> CreateAsync(
            PermissionOptions? permissions = null,
            IReadOnlyList<IPlugin>? plugins = null)
        {
            AdminTestDatabase database = await AdminTestDatabase.CreateAsync();

            var harness = new Harness(
                database,
                new MutableTimeProvider(Start),
                permissions ?? new PermissionOptions(),
                plugins ?? []);

            await harness.Service.InitializeAsync(CancellationToken.None);

            return harness;
        }

        /// <summary>模拟宿主重启：数据库不变，其余全部重建后重新装载。</summary>
        public async Task RestartAsync()
        {
            _components = CreateComponents();

            await Service.InitializeAsync(CancellationToken.None);
        }

        /// <summary>读取全部审计记录，按写入先后排列。</summary>
        public async Task<IReadOnlyList<AuditLog>> ListAuditAsync()
        {
            await using AdminDbContext db = Database.Factory.CreateDbContext();

            return await db.AuditLogs
                .AsNoTracking()
                .OrderBy(log => log.Id)
                .ToListAsync();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();

        /// <summary>装配这一代的全部内存对象。</summary>
        private Components CreateComponents()
        {
            var lists = new PermissionListStore(Database.Factory, Clock);
            var pluginStore = new PluginStateStore(Database.Factory, Clock);
            var accessPolicy = new InMemoryAccessPolicy(Options.Create(_permissions));
            var pluginState = new PluginStateRegistry();
            PluginManager plugins = CreatePluginManager(_plugins, pluginState);

            var service = new RuntimeControlService(
                lists,
                pluginStore,
                accessPolicy,
                pluginState,
                plugins,
                Options.Create(_permissions),
                new AuditLogStore(Database.Factory, Clock, NullLogger<AuditLogStore>.Instance),
                NullLogger<RuntimeControlService>.Instance);

            return new Components(service, accessPolicy, pluginState, plugins, lists, pluginStore);
        }

        /// <summary>构成「一代」内核侧与持久化侧对象的集合。</summary>
        private sealed record Components(
            RuntimeControlService Service,
            InMemoryAccessPolicy AccessPolicy,
            PluginStateRegistry PluginState,
            PluginManager Plugins,
            IPermissionListStore Lists,
            IPluginStateStore PluginStore);

        /// <summary>装配插件管理器：真实的分发器与状态注册表，中间件为空。</summary>
        private static PluginManager CreatePluginManager(
            IReadOnlyList<IPlugin> plugins,
            PluginStateRegistry state)
        {
            var dispatcher = new CommandDispatcher(
                Options.Create(new CommandOptions()),
                new InMemoryPermissionService(
                    new InMemoryAccessPolicy(Options.Create(new PermissionOptions())),
                    NullLogger<InMemoryPermissionService>.Instance),
                state,
                NullLogger<CommandDispatcher>.Instance);

            foreach (IPlugin plugin in plugins)
            {
                dispatcher.RegisterPluginCommands(plugin);
            }

            return new PluginManager(plugins, [], dispatcher, state, NullLogger<PluginManager>.Instance);
        }
    }

    /// <summary>
    /// 取插件类型全名，口径与命令注册时写入的插件名一致——
    /// 后台传的类型名必须与之分毫不差，否则门控会静默失效。
    /// </summary>
    private static string TypeNameOf(IPlugin plugin)
    {
        Type type = plugin.GetType();
        return type.FullName ?? type.Name;
    }

    /// <summary>测试用插件：注册一个命令，仅用于让插件管理器能认出它。</summary>
    private sealed class DemoPlugin : IPlugin
    {
        [OnCommand("echo")]
        public ValueTask EchoAsync(CommandContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
