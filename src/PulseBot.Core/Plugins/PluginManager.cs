using System.Reflection;
using Microsoft.Extensions.Logging;
using PulseBot.Core.Commands;

namespace PulseBot.Core.Plugins;

/// <summary>
/// 插件生命周期管理器：统一加载/卸载所有已注册插件，并保证异常隔离——
/// 单个插件加载或初始化失败只记录日志并跳过，不会导致宿主启动失败或运行期崩溃。
/// </summary>
/// <remarks>
/// <para>由宿主显式驱动（Core 不引用 Hosting 抽象，不实现 IHostedService）：</para>
/// <code>
/// var app = builder.Build();
/// var pluginManager = app.Services.GetRequiredService&lt;PluginManager&gt;();
/// await pluginManager.LoadAllAsync(app.Lifetime.ApplicationStopping);
/// // ... 运行 ...
/// await pluginManager.UnloadAllAsync(CancellationToken.None); // 优雅关闭
/// </code>
/// <para>
/// 命令表条目与宿主生命周期一致，本步骤不随单个插件卸载移除。
/// </para>
/// </remarks>
public sealed class PluginManager
{
    /// <summary>容器中注册的全部插件（顺序即注册顺序，决定命令冲突时的「先注册者胜」）。</summary>
    private readonly IReadOnlyList<IPlugin> _plugins;

    /// <summary>命令分发器（装配期需要注册能力，因此依赖具体类型而非只读接口）。</summary>
    private readonly CommandDispatcher _commandDispatcher;

    private readonly ILogger<PluginManager> _logger;

    /// <summary>已成功加载的插件，卸载时按逆序处理。</summary>
    private readonly List<IPlugin> _loadedPlugins = [];

    private bool _loaded;

    /// <summary>
    /// 创建插件管理器。由 DI 注入全部 <see cref="IPlugin"/> 注册项与命令分发器。
    /// </summary>
    public PluginManager(
        IEnumerable<IPlugin> plugins,
        CommandDispatcher commandDispatcher,
        ILogger<PluginManager> logger)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        // 物化一次，避免每次枚举时重新解析容器。
        _plugins = plugins as IReadOnlyList<IPlugin> ?? plugins.ToArray();
        _commandDispatcher = commandDispatcher;
        _logger = logger;
    }

    /// <summary>已成功加载的插件列表（只读，用于诊断）。</summary>
    public IReadOnlyList<IPlugin> LoadedPlugins => _loadedPlugins;

    /// <summary>
    /// 加载全部插件：逐个执行「调用 OnLoadAsync 初始化 → 扫描 [OnCommand] 并注册命令」，
    /// 任何插件失败都被隔离（记录日志并跳过，且不会注册它的命令），其余插件照常加载。
    /// 重复调用是幂等的（第二次直接返回）。
    /// </summary>
    /// <param name="cancellationToken">启动期取消令牌。</param>
    public async ValueTask LoadAllAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        foreach (var plugin in _plugins)
        {
            var (displayName, version) = Describe(plugin);

            try
            {
                // 步骤 1：插件自身的异步初始化。
                await plugin.OnLoadAsync(cancellationToken).ConfigureAwait(false);

                // 步骤 2：扫描 [OnCommand] 并把处理器编译为强类型委托缓存（反射仅此一次）。
                // 放在初始化成功之后，保证加载失败的插件不会对外留下「僵尸命令」。
                _commandDispatcher.RegisterPluginCommands(plugin);

                _loadedPlugins.Add(plugin);
                _logger.LogInformation("插件 {PluginName} v{PluginVersion} 加载完成。", displayName, version);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 宿主启动被取消：正常控制流，向上传播。
                throw;
            }
            catch (Exception ex)
            {
                // 异常隔离：记录插件身份与异常详情，跳过该插件，其余插件与宿主不受影响。
                _logger.LogError(ex, "插件 {PluginName} 加载失败，已跳过。", displayName);
            }
        }
    }

    /// <summary>
    /// 卸载全部已加载插件（逆序）。卸载阶段的异常与取消都只记录日志并继续，
    /// 保证「优雅关闭」尽力清理每一个插件。
    /// </summary>
    /// <param name="cancellationToken">关闭期取消令牌。</param>
    public async ValueTask UnloadAllAsync(CancellationToken cancellationToken)
    {
        for (var i = _loadedPlugins.Count - 1; i >= 0; i--)
        {
            var plugin = _loadedPlugins[i];
            var (displayName, _) = Describe(plugin);

            try
            {
                await plugin.OnUnloadAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("插件 {PluginName} 已卸载。", displayName);
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

    /// <summary>读取插件显示名与版本：优先取 <see cref="PluginMetadataAttribute"/>，缺省回退到类型名。</summary>
    private static (string DisplayName, string Version) Describe(IPlugin plugin)
    {
        var pluginType = plugin.GetType();
        var metadata = pluginType.GetCustomAttribute<PluginMetadataAttribute>();
        return (metadata?.Name ?? pluginType.FullName ?? pluginType.Name, metadata?.Version ?? "未知");
    }
}
