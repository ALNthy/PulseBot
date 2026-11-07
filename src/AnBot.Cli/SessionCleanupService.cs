using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Core.Sessions;

namespace AnBot.Cli;

/// <summary>
/// 宿主侧的会话清理服务：按固定间隔清除空闲超时的交互会话。
/// </summary>
/// <remarks>
/// <para>
/// Core 不依赖 Hosting 抽象，<see cref="ISessionStore"/> 自身不提供后台清理，
/// 由本类在宿主侧用 <see cref="PeriodicTimer"/> 驱动
/// <see cref="ISessionStore.CleanupExpiredAsync"/>。
/// </para>
/// <para>
/// 不做清理也不会读到过期状态——存储会在下次访问时把过期会话懒丢弃。
/// 本服务的价值是及时回收「再也不说话的用户」所占用的内存。
/// </para>
/// </remarks>
internal sealed class SessionCleanupService : BackgroundService
{
    private readonly ISessionStore _sessions;
    private readonly TimeSpan _interval;
    private readonly ILogger<SessionCleanupService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="sessions">会话存储。</param>
    /// <param name="options">会话配置（清理间隔）。</param>
    /// <param name="logger">结构化日志。</param>
    public SessionCleanupService(
        ISessionStore sessions,
        IOptions<SessionOptions> options,
        ILogger<SessionCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
        _interval = options.Value.CleanupInterval;
        _logger = logger;
    }

    /// <summary>按固定间隔清理，直到宿主要求停止。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 间隔配置为非正值时视为关闭清理：安静地结束，不占用后台线程。
        if (_interval <= TimeSpan.Zero)
        {
            _logger.LogInformation("会话清理间隔配置为非正值，后台清理已关闭。");
            return;
        }

        _logger.LogInformation("会话清理服务已启动，每 {Interval} 回收一次空闲超时的交互会话。", _interval);

        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await CleanupOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正在关闭，正常退出；关闭播报由 AnBotLifecycleService 负责。
        }
    }

    /// <summary>执行一轮清理，异常隔离：单次失败不应终止整个服务。</summary>
    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await _sessions.CleanupExpiredAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogDebug("已回收 {Count} 个空闲超时的交互会话。", removed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理空闲会话时发生异常，将在下个周期重试。");
        }
    }
}
