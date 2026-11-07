using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AnBot.Core.Adapters;
using AnBot.Core.Commands;
using AnBot.Core.Plugins;

namespace AnBot.Cli;

/// <summary>
/// 宿主生命周期协调器：把「插件加载/卸载」与「适配器启停」挂到 .NET 通用主机的启动/关闭流程上。
/// </summary>
/// <remarks>
/// <para>
/// Core 不依赖 Hosting 抽象（因此不实现 <c>IHostedService</c>），由本类在宿主侧完成编排。
/// </para>
/// <para>顺序是有意为之：</para>
/// <list type="bullet">
///   <item><description>启动：先加载插件（命令表就绪），再启动适配器（此后才会有事件进来）。</description></item>
///   <item><description>关闭：先停适配器（不再接收新事件），再卸载插件（释放资源），即启动的逆序。</description></item>
/// </list>
/// <para>
/// 适配器数量取决于宿主注册了几个：默认宿主同时注册控制台与 OneBot 12 两个适配器；
/// 一个都不注册时本服务退化为「就绪状态播报器」，仍可用于验证装配链路是否打通。
/// </para>
/// </remarks>
internal sealed class AnBotLifecycleService : IHostedService
{
    private readonly PluginManager _pluginManager;
    private readonly ICommandDispatcher _commandDispatcher;
    private readonly IReadOnlyList<IAdapter> _adapters;
    private readonly ILogger<AnBotLifecycleService> _logger;

    /// <summary>构造函数注入。</summary>
    public AnBotLifecycleService(
        PluginManager pluginManager,
        ICommandDispatcher commandDispatcher,
        IEnumerable<IAdapter> adapters,
        ILogger<AnBotLifecycleService> logger)
    {
        ArgumentNullException.ThrowIfNull(pluginManager);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(logger);

        _pluginManager = pluginManager;
        _commandDispatcher = commandDispatcher;
        _logger = logger;

        // 物化一次：DI 提供的 IEnumerable 每次枚举都会重新解析服务。
        _adapters = adapters as IReadOnlyList<IAdapter> ?? adapters.ToArray();
    }

    /// <summary>启动：加载插件 → 启动适配器 → 播报就绪状态。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _pluginManager.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (IAdapter adapter in _adapters)
        {
            await adapter.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("适配器 {AdapterName}（平台 {Platform}）已启动。", adapter.Name, adapter.Platform);
        }

        _logger.LogInformation(
            "AnBot 已就绪：{PluginCount} 个插件、{CommandCount} 条命令、{AdapterCount} 个适配器。",
            _pluginManager.LoadedPlugins.Count,
            _commandDispatcher.Commands.Count,
            _adapters.Count);

        if (_adapters.Count == 0)
        {
            _logger.LogWarning("当前没有注册任何适配器，AnBot 收不到平台事件，仅处于待命状态。");
        }
    }

    /// <summary>关闭：停适配器（逆序）→ 卸载插件。异常逐项隔离，保证尽力清理。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var i = _adapters.Count - 1; i >= 0; i--)
        {
            IAdapter adapter = _adapters[i];

            try
            {
                await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("适配器 {AdapterName} 已停止。", adapter.Name);
            }
            catch (Exception ex)
            {
                // 优雅关闭期间不因单个适配器的清理失败而中断其余清理工作。
                _logger.LogError(ex, "适配器 {AdapterName} 停止时发生异常，已忽略。", adapter.Name);
            }
        }

        // PluginManager 内部已对每个插件的卸载异常做了隔离。
        await _pluginManager.UnloadAllAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("AnBot 已关闭。");
    }
}
