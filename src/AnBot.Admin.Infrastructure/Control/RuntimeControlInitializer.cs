using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Infrastructure.Control;

/// <summary>
/// 启动期把持久化的运行时控制状态（权限名单、插件启停）装载进内核。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="AdminDatabaseInitializer"/> 分成两个宿主服务，是因为它们的职责与
/// 失败语义都不同：建库失败意味着后台根本不可用（应当让宿主启动失败），
/// 而装载运行时状态失败只影响「名单是否跟随数据库」这一件事。
/// </para>
/// <para>
/// <b>顺序依赖</b>：本服务必须在 <see cref="AdminDatabaseInitializer"/> **之后**启动，
/// 因为它要读的表由后者创建。这一点靠 DI 的注册顺序保证——
/// <c>HostedService</c> 按注册顺序依次启动，改动注册顺序会破坏这个前提。
/// </para>
/// <para>
/// 装载必须在机器人开始处理消息之前完成；否则从启动到装载之间的窗口里，
/// 判定用的会是配置文件里的旧名单。
/// </para>
/// </remarks>
public sealed class RuntimeControlInitializer : IHostedService
{
    private readonly IRuntimeControlService _runtimeControl;
    private readonly AdminOptions _options;
    private readonly ILogger<RuntimeControlInitializer> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="runtimeControl">运行时控制服务。</param>
    /// <param name="options">后台配置。</param>
    /// <param name="logger">结构化日志。</param>
    public RuntimeControlInitializer(
        IRuntimeControlService runtimeControl,
        IOptions<AdminOptions> options,
        ILogger<RuntimeControlInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(runtimeControl);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runtimeControl = runtimeControl;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            // 后台关闭：名单与插件状态完全由配置文件决定，内核侧保持播种时的初值。
            // 这也意味着「在后台改过的名单」在关闭后台后会回到配置文件的值——
            // 这是可预期的：关闭后台就等于不使用后台维护的那份状态。
            _logger.LogInformation(
                "后台管理已关闭，跳过运行时控制状态的装载：权限名单与插件启停以配置文件为准。");

            return;
        }

        await _runtimeControl.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
