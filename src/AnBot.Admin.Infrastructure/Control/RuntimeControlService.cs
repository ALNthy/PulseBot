using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Shared.Accounts;
using AnBot.Admin.Shared.Control;
using AnBot.Core.Permissions;
using AnBot.Core.Plugins;

namespace AnBot.Admin.Infrastructure.Control;

/// <summary>
/// 运行时控制：权限名单与插件启停的读取、持久化，以及「改完立即生效」。
/// </summary>
/// <remarks>
/// <para>
/// 这是「后台改 → 内核立即生效」这条链唯一的落点。数据库是权威来源，
/// 配置文件只在**库中还没有这份名单**时用于播种；一旦播种完成，
/// 该名单的权威来源就转移到了数据库（因此把名单清空后重启不会被配置重新填满）。
/// </para>
/// <para>
/// 内核侧只暴露两个整体替换的入口——<see cref="IAccessPolicy.Update"/> 与
/// <see cref="PluginStateRegistry.Replace"/>——本类负责把库里的状态整理成它们要的形状。
/// </para>
/// </remarks>
public interface IRuntimeControlService
{
    /// <summary>
    /// 启动装载：把持久化的名单与插件启停状态推入内核，并为库中尚不存在的名单播种。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>读取当前生效的接入策略。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AccessPolicyInfo> GetAccessPolicyAsync(CancellationToken cancellationToken);

    /// <summary>整体替换一份名单并立即生效。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="listKey">名单标识。</param>
    /// <param name="values">新的完整名单。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<RuntimeControlStatus> UpdateListAsync(
        AccountActor actor,
        string listKey,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken);

    /// <summary>启用或停用一个插件并立即生效。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="pluginTypeName">插件类型全名。</param>
    /// <param name="enabled">目标状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<RuntimeControlStatus> SetPluginEnabledAsync(
        AccountActor actor,
        string pluginTypeName,
        bool enabled,
        CancellationToken cancellationToken);
}

/// <summary>基于 EF Core 与 Core 运行时持有者的控制实现。</summary>
public sealed class RuntimeControlService : IRuntimeControlService
{
    private readonly IPermissionListStore _lists;
    private readonly IPluginStateStore _pluginStore;
    private readonly IAccessPolicy _accessPolicy;
    private readonly PluginStateRegistry _pluginState;
    private readonly PluginManager _plugins;
    private readonly PermissionOptions _permissions;
    private readonly IAuditLogStore _audit;
    private readonly ILogger<RuntimeControlService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="lists">权限名单存储。</param>
    /// <param name="pluginStore">插件启停状态存储。</param>
    /// <param name="accessPolicy">内核侧的接入策略持有者。</param>
    /// <param name="pluginState">内核侧的插件状态注册表。</param>
    /// <param name="plugins">插件管理器（确认插件存在、切换命令门控）。</param>
    /// <param name="permissions">权限配置（仅用于播种与提示文本）。</param>
    /// <param name="audit">审计日志存储。</param>
    /// <param name="logger">结构化日志。</param>
    public RuntimeControlService(
        IPermissionListStore lists,
        IPluginStateStore pluginStore,
        IAccessPolicy accessPolicy,
        PluginStateRegistry pluginState,
        PluginManager plugins,
        IOptions<PermissionOptions> permissions,
        IAuditLogStore audit,
        ILogger<RuntimeControlService> logger)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(pluginStore);
        ArgumentNullException.ThrowIfNull(accessPolicy);
        ArgumentNullException.ThrowIfNull(pluginState);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _lists = lists;
        _pluginStore = pluginStore;
        _accessPolicy = accessPolicy;
        _pluginState = pluginState;
        _plugins = plugins;
        _permissions = permissions.Value;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string[]> stored = await _lists
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        // 库里没有的名单按配置播种，并立刻落库：否则管理员打开后台会看到一份空名单，
        // 而他从未清空过任何东西，更糟的是下一次启动会再播种一遍——
        // 于是「后台里删掉的条目」会在重启后复活。
        Dictionary<string, string[]> effective = new(stored, StringComparer.Ordinal);
        List<string> seeded = [];

        foreach (string key in PermissionListKeys.All)
        {
            if (effective.ContainsKey(key))
            {
                continue;
            }

            string[] values = SeedFromConfiguration(key);
            await _lists.UpsertAsync(key, values, cancellationToken).ConfigureAwait(false);

            effective[key] = values;
            seeded.Add(key);
        }

        _accessPolicy.Update(BuildSnapshot(effective));

        IReadOnlyList<string> disabled = await _pluginStore
            .GetDisabledAsync(cancellationToken)
            .ConfigureAwait(false);

        _pluginState.Replace(disabled);

        _logger.LogInformation(
            "运行时控制状态已装载：权限名单 {ListCount} 份（其中 {SeededCount} 份由配置播种），"
            + "停用插件 {DisabledCount} 个。",
            effective.Count,
            seeded.Count,
            disabled.Count);

        if (seeded.Count > 0)
        {
            _logger.LogInformation(
                "以下名单此前未持久化，已按配置写入数据库并成为权威来源：{SeededKeys}。"
                + "此后它们不再跟随配置文件变化。",
                string.Join("、", seeded));
        }
    }

    /// <inheritdoc />
    public ValueTask<AccessPolicyInfo> GetAccessPolicyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AccessPolicySnapshot current = _accessPolicy.Current;

        // 顺序与 PermissionListKeys.All 一致：读取接口返回的顺序就是后台的展示顺序，
        // 两处各排一次的话，早晚会出现「界面上的顺序与标识的声明顺序不符」。
        return ValueTask.FromResult(new AccessPolicyInfo(
            Lists:
            [
                new PermissionListInfo(PermissionListKeys.SuperAdmins, [.. current.SuperAdmins]),
                new PermissionListInfo(PermissionListKeys.UserBlacklist, [.. current.UserBlacklist]),
                new PermissionListInfo(PermissionListKeys.UserWhitelist, [.. current.UserWhitelist]),
                new PermissionListInfo(PermissionListKeys.GroupBlacklist, [.. current.GroupBlacklist]),
                new PermissionListInfo(PermissionListKeys.GroupWhitelist, [.. current.GroupWhitelist]),
            ],
            DeniedMessage: current.DeniedMessage));
    }

    /// <inheritdoc />
    public async ValueTask<RuntimeControlStatus> UpdateListAsync(
        AccountActor actor,
        string listKey,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(values);

        if (!PermissionListKeys.IsKnown(listKey))
        {
            return RuntimeControlStatus.UnknownList;
        }

        await _lists.UpsertAsync(listKey, values, cancellationToken).ConfigureAwait(false);

        // 落库后重新读齐全部名单再构造快照，而不是「读当前快照 + 替换其中一份」：
        // 后者在两个管理员同时改不同名单时会丢掉先完成的那次改动。
        // 单次写入换来的这点开销，在人工操作频率下无关紧要。
        IReadOnlyDictionary<string, string[]> stored = await _lists
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        _accessPolicy.Update(BuildSnapshot(stored));

        await _audit.RecordAsync(
            new AuditEntry(
                Action: "control.update-list",
                ActorName: actor.UserName,
                TargetType: "permission-list",
                TargetId: listKey,
                Detail: $"已更新名单 {listKey}，共 {values.Count} 条",
                RemoteIp: actor.RemoteIp),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "{ActorName} 更新了权限名单 {ListKey}（{Count} 条），已立即生效。",
            actor.UserName,
            listKey,
            values.Count);

        return RuntimeControlStatus.Success;
    }

    /// <inheritdoc />
    public async ValueTask<RuntimeControlStatus> SetPluginEnabledAsync(
        AccountActor actor,
        string pluginTypeName,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);

        if (!_plugins.ContainsPlugin(pluginTypeName))
        {
            return RuntimeControlStatus.PluginNotFound;
        }

        // 先落库、再改内存：落库失败时内存状态与库里都还是旧值，两者始终一致。
        // 反过来则会出现「本次运行已生效、重启后却回退」的错位。
        await _pluginStore
            .SetEnabledAsync(pluginTypeName, enabled, cancellationToken)
            .ConfigureAwait(false);

        // 存在性刚刚确认过，这里的返回值不可能为 false；插件名与动作由 PluginManager 自行记录。
        _plugins.SetPluginEnabled(pluginTypeName, enabled);

        await _audit.RecordAsync(
            new AuditEntry(
                Action: "control.set-plugin-state",
                ActorName: actor.UserName,
                TargetType: "plugin",
                TargetId: pluginTypeName,
                Detail: enabled ? "已启用插件" : "已停用插件",
                RemoteIp: actor.RemoteIp),
            cancellationToken).ConfigureAwait(false);

        return RuntimeControlStatus.Success;
    }

    /// <summary>从当前全部名单构造一份新的策略快照。</summary>
    private AccessPolicySnapshot BuildSnapshot(IReadOnlyDictionary<string, string[]> lists)
        => new(
            superAdmins: ValuesOf(lists, PermissionListKeys.SuperAdmins),
            userBlacklist: ValuesOf(lists, PermissionListKeys.UserBlacklist),
            userWhitelist: ValuesOf(lists, PermissionListKeys.UserWhitelist),
            groupBlacklist: ValuesOf(lists, PermissionListKeys.GroupBlacklist),
            groupWhitelist: ValuesOf(lists, PermissionListKeys.GroupWhitelist),
            deniedMessage: _permissions.DeniedMessage);

    /// <summary>取某份名单，缺失时视为空名单。</summary>
    private static string[] ValuesOf(IReadOnlyDictionary<string, string[]> lists, string key)
        => lists.TryGetValue(key, out string[]? values) ? values : [];

    /// <summary>取某份名单在配置文件里的初始值，用于首次播种。</summary>
    private string[] SeedFromConfiguration(string key) => key switch
    {
        PermissionListKeys.SuperAdmins => [.. _permissions.SuperAdmins],
        PermissionListKeys.UserBlacklist => [.. _permissions.UserBlacklist],
        PermissionListKeys.UserWhitelist => [.. _permissions.UserWhitelist],
        PermissionListKeys.GroupBlacklist => [.. _permissions.GroupBlacklist],
        PermissionListKeys.GroupWhitelist => [.. _permissions.GroupWhitelist],

        // 不可达：调用方只会传入 PermissionListKeys.All 中的键。
        _ => [],
    };
}
