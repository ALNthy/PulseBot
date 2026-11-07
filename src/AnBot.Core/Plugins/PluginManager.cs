using System.Reflection;
using Microsoft.Extensions.Logging;
using AnBot.Core.Commands;

namespace AnBot.Core.Plugins;

/// <summary>
/// 插件生命周期管理器：统一加载/卸载所有已注册插件，并保证异常隔离——
/// 单个插件加载或初始化失败只记录日志并跳过，不会导致宿主启动失败或运行期崩溃。
/// </summary>
/// <remarks>
/// <para>由宿主显式驱动（Core 不引用 Hosting 抽象，不实现 IHostedService）：</para>
/// <code>
/// var app = builder.Build();
/// var pluginManager = app.Services.GetRequiredService&lt;PluginManager&gt;();
/// await pluginManager.LoadAllAsync(startupToken);                        // 加载：启动期令牌
/// // ... 运行 ...
/// await pluginManager.UnloadAllAsync(app.Lifetime.ApplicationStopping);  // 卸载：关闭期令牌
/// </code>
/// <para>
/// 两个阶段用的令牌不同：加载发生在启动阶段，此时 <c>ApplicationStopping</c> 尚未触发，
/// 用它会让「启动中途收到关闭信号」无法中断加载；卸载才该监听 <c>ApplicationStopping</c>。
/// </para>
/// <para>
/// 命令表条目与宿主生命周期一致：插件的启停（<see cref="SetPluginEnabled"/>）不增删条目，
/// 只切换「是否可触发」，因此命令表在启动完成后始终是只读的。
/// </para>
/// </remarks>
public sealed class PluginManager
{
    /// <summary>容器中注册的全部插件（顺序即注册顺序，决定命令冲突时的「先注册者胜」）。</summary>
    private readonly IReadOnlyList<IPlugin> _plugins;

    /// <summary>生命周期中间件，构造期已按优先级降序固定。</summary>
    private readonly IReadOnlyList<IPluginLifecycleMiddleware> _middlewares;

    /// <summary>命令分发器（装配期需要注册能力，因此依赖具体类型而非只读接口）。</summary>
    private readonly CommandDispatcher _commandDispatcher;

    /// <summary>插件启用状态：由后台管理驱动，命令分发据此门控。</summary>
    private readonly PluginStateRegistry _state;

    private readonly ILogger<PluginManager> _logger;

    /// <summary>已成功加载的插件，卸载时按逆序处理。</summary>
    private readonly List<IPlugin> _loadedPlugins = [];

    /// <summary>加载与卸载各自的管道入口；未注册任何中间件时为 null，走与引入中间件前完全一致的路径。</summary>
    private readonly PluginLifecycleNext? _loadPipeline;
    private readonly PluginLifecycleNext? _unloadPipeline;

    private bool _loaded;

    /// <summary>
    /// 创建插件管理器。由 DI 注入全部 <see cref="IPlugin"/> 注册项、生命周期中间件与命令分发器。
    /// </summary>
    public PluginManager(
        IEnumerable<IPlugin> plugins,
        IEnumerable<IPluginLifecycleMiddleware> middlewares,
        CommandDispatcher commandDispatcher,
        PluginStateRegistry state,
        ILogger<PluginManager> logger)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(middlewares);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);

        // 物化一次，避免每次枚举时重新解析容器。
        _plugins = plugins as IReadOnlyList<IPlugin> ?? plugins.ToArray();
        _middlewares = middlewares.OrderByDescending(static middleware => middleware.Priority).ToArray();
        _commandDispatcher = commandDispatcher;
        _state = state;
        _logger = logger;

        _loadPipeline = BuildPipeline(PluginLifecyclePhase.Load);
        _unloadPipeline = BuildPipeline(PluginLifecyclePhase.Unload);
    }

    /// <summary>已成功加载的插件列表（只读，用于诊断）。</summary>
    public IReadOnlyList<IPlugin> LoadedPlugins => _loadedPlugins;

    /// <summary>容器中登记的全部插件（含被停用的），供后台列举与按类型名查找。</summary>
    public IReadOnlyList<IPlugin> Plugins => _plugins;

    /// <summary>判断插件当前是否启用（未登记过的类型名视为启用）。</summary>
    /// <param name="pluginTypeName">插件类型全名。</param>
    public bool IsEnabled(string pluginTypeName) => !_state.IsDisabled(pluginTypeName);

    /// <summary>容器中是否登记了该类型名的插件（不论当前启用与否）。</summary>
    /// <param name="pluginTypeName">插件类型全名。</param>
    /// <remarks>
    /// 供调用方在改变状态之前先确认目标存在——把「目标不存在」与「写入失败」分开，
    /// 前者是明确的业务拒绝，后者是故障，两者不该混在同一个返回值里。
    /// </remarks>
    public bool ContainsPlugin(string pluginTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);

        return FindPlugin(pluginTypeName) is not null;
    }

    /// <summary>
    /// 启用或停用一个插件：停用后它注册的命令立即不再可触发，启用后立即恢复。
    /// </summary>
    /// <param name="pluginTypeName">插件类型全名（与 <c>PluginInfo.TypeName</c> 同口径）。</param>
    /// <param name="enabled"><c>true</c> 为启用，<c>false</c> 为停用。</param>
    /// <returns>容器中存在该插件时返回 <c>true</c>；类型名对不上任何已登记插件时返回 <c>false</c>。</returns>
    /// <remarks>
    /// <para>
    /// 只切换命令门控，<b>不改动生命周期</b>：插件实例、它占用的资源与它注册的事件处理器都不受影响
    /// （<see cref="IPlugin.OnUnloadAsync"/> 不会被调用）。这是刻意的——Core 既无法保证插件能被安全地
    /// 热卸载，也不掌握「插件与事件处理器」的归属关系，因此不给出做不到的承诺。
    /// </para>
    /// <para>
    /// 重复设置同一状态不算失败：返回值只表示「插件是否存在」，是否真的发生了变化由内部状态决定。
    /// </para>
    /// </remarks>
    public bool SetPluginEnabled(string pluginTypeName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);

        IPlugin? plugin = FindPlugin(pluginTypeName);
        if (plugin is null)
        {
            return false;
        }

        if (!_state.SetEnabled(pluginTypeName, enabled))
        {
            // 状态本就如此：幂等操作，不产生日志噪音。
            return true;
        }

        var (displayName, _) = Describe(plugin);
        _logger.LogInformation(
            "插件 {PluginName}（{PluginTypeName}）已被{Action}，其命令{Effect}。",
            displayName,
            pluginTypeName,
            enabled ? "启用" : "停用",
            enabled ? "恢复可触发" : "不再可触发");

        return true;
    }

    /// <summary>按类型全名查找插件实例；找不到返回 <c>null</c>。</summary>
    private IPlugin? FindPlugin(string pluginTypeName)
    {
        foreach (IPlugin plugin in _plugins)
        {
            if (string.Equals(GetTypeName(plugin), pluginTypeName, StringComparison.Ordinal))
            {
                return plugin;
            }
        }

        return null;
    }

    /// <summary>
    /// 加载全部插件：逐个执行「调用 OnLoadAsync 初始化 → 扫描 [OnCommand] 并注册命令」，
    /// 任何插件失败都被隔离（记录日志并跳过，且不会注册它的命令），其余插件照常加载。
    /// 重复调用是幂等的（第二次直接返回）。
    /// </summary>
    /// <remarks>
    /// 每个插件先经过生命周期中间件链再到上述两个步骤；中间件不调用续延即跳过该插件
    /// （不初始化、不注册命令、不计入已加载列表，也不会打印加载完成日志）。
    /// 中间件的异常与插件自身的异常同样在此隔离。
    /// </remarks>
    /// <param name="cancellationToken">启动期取消令牌。</param>
    public async ValueTask LoadAllAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        foreach (IPlugin plugin in _plugins)
        {
            var (displayName, version) = Describe(plugin);
            var context = new PluginLifecycleContext
            {
                Plugin = plugin,
                Phase = PluginLifecyclePhase.Load,
                PluginName = displayName,
                PluginVersion = version,
            };

            try
            {
                if (_loadPipeline is null)
                {
                    // 未装中间件：直接走终端，与引入中间件之前完全一致。
                    await LoadCoreAsync(context, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _loadPipeline(context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 宿主启动被取消：正常控制流，向上传播。
                throw;
            }
            catch (Exception ex)
            {
                // 异常隔离：记录插件身份与异常详情，跳过该插件，其余插件与宿主不受影响。
                // 管道中间件的异常也在此收口——插件侧管道刻意不自捕获，以便中间件做失败补偿。
                _logger.LogError(ex, "插件 {PluginName} 加载失败，已跳过。", displayName);
            }
        }
    }

    /// <summary>
    /// 加载管道的内层终端：调用插件钩子，成功后再把命令注册进命令表。
    /// </summary>
    private async ValueTask LoadCoreAsync(PluginLifecycleContext context, CancellationToken cancellationToken)
    {
        // 步骤 1：插件自身的异步初始化。
        await context.Plugin.OnLoadAsync(cancellationToken).ConfigureAwait(false);

        // 步骤 2：扫描 [OnCommand] 并把处理器编译为强类型委托缓存（反射仅此一次）。
        // 放在初始化成功之后，保证加载失败的插件不会对外留下「僵尸命令」。
        _commandDispatcher.RegisterPluginCommands(context.Plugin);

        _loadedPlugins.Add(context.Plugin);
        _logger.LogInformation(
            "插件 {PluginName} v{PluginVersion} 加载完成。",
            context.PluginName,
            context.PluginVersion);
    }

    /// <summary>
    /// 卸载全部已加载插件（逆序）。卸载阶段的异常与取消都只记录日志并继续，
    /// 保证「优雅关闭」尽力清理每一个插件。
    /// </summary>
    /// <remarks>
    /// 每个插件先经过生命周期中间件链再到卸载钩子；中间件不调用续延即跳过该插件的清理。
    /// 中间件的异常与插件自身的异常同样在此隔离。
    /// </remarks>
    /// <param name="cancellationToken">关闭期取消令牌。</param>
    public async ValueTask UnloadAllAsync(CancellationToken cancellationToken)
    {
        for (var i = _loadedPlugins.Count - 1; i >= 0; i--)
        {
            IPlugin plugin = _loadedPlugins[i];
            var (displayName, version) = Describe(plugin);
            var context = new PluginLifecycleContext
            {
                Plugin = plugin,
                Phase = PluginLifecyclePhase.Unload,
                PluginName = displayName,
                PluginVersion = version,
            };

            try
            {
                if (_unloadPipeline is null)
                {
                    // 未装中间件：直接走终端，与引入中间件之前完全一致。
                    await UnloadCoreAsync(context, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _unloadPipeline(context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("插件 {PluginName} 卸载时被取消，继续卸载其余插件。", displayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "插件 {PluginName} 卸载失败，已忽略。", displayName);
            }
        }

        _loadedPlugins.Clear();
        _loaded = false;
    }

    /// <summary>卸载管道的内层终端：调用插件的卸载钩子。</summary>
    private async ValueTask UnloadCoreAsync(PluginLifecycleContext context, CancellationToken cancellationToken)
    {
        await context.Plugin.OnUnloadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("插件 {PluginName} 已卸载。", context.PluginName);
    }

    /// <summary>
    /// 按阶段把中间件由外到内包裹成一条链；未注册任何中间件时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 与事件、出站两条管道相同：全部包装在构造期完成，加载/卸载时只剩纯委托调用。
    /// </remarks>
    private PluginLifecycleNext? BuildPipeline(PluginLifecyclePhase phase)
    {
        if (_middlewares.Count == 0)
        {
            return null;
        }

        // 终端由阶段决定；方法组转换即委托，不产生额外的闭包。
        PluginLifecycleNext next = phase == PluginLifecyclePhase.Load ? LoadCoreAsync : UnloadCoreAsync;

        // 反向包裹：优先级最低的先成为内层，循环结束时 next 即优先级最高者的入口。
        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            next = WrapMiddleware(_middlewares[i], next);
        }

        return next;
    }

    /// <summary>
    /// 把单个中间件包成一层。刻意不捕获异常：让逐插件的隔离逻辑统一收口，
    /// 同时保证中间件能用 try/catch 包住续延来做失败补偿。
    /// </summary>
    private static PluginLifecycleNext WrapMiddleware(IPluginLifecycleMiddleware middleware, PluginLifecycleNext next)
        => (context, cancellationToken) => middleware.InvokeAsync(context, next, cancellationToken);

    /// <summary>
    /// 读取插件类型全名。必须与 <see cref="CommandDispatcher.RegisterPluginCommands"/> 写入
    /// <c>CommandRegistration.PluginName</c> 时的取法一致，门控才匹配得上。
    /// </summary>
    private static string GetTypeName(IPlugin plugin)
    {
        var pluginType = plugin.GetType();
        return pluginType.FullName ?? pluginType.Name;
    }

    /// <summary>读取插件显示名与版本：优先取 <see cref="PluginMetadataAttribute"/>，缺省回退到类型名。</summary>
    private static (string DisplayName, string Version) Describe(IPlugin plugin)
    {
        var pluginType = plugin.GetType();
        var metadata = pluginType.GetCustomAttribute<PluginMetadataAttribute>();
        return (metadata?.Name ?? pluginType.FullName ?? pluginType.Name, metadata?.Version ?? "未知");
    }
}
