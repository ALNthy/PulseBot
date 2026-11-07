using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Core.Scheduling;

namespace AnBot.Cli;

/// <summary>
/// 宿主侧的定时任务调度服务：按固定间隔把到期任务跑起来。
/// </summary>
/// <remarks>
/// <para>
/// Core 不依赖 Hosting 抽象，<see cref="Scheduler"/> 自身不提供后台循环，
/// 由本类在宿主侧用 <see cref="PeriodicTimer"/> 驱动
/// <see cref="Scheduler.RunDueTasksAsync"/>——与 <see cref="SessionCleanupService"/> 同一分工。
/// </para>
/// <para>
/// <b>节拍与到期判定用的是两个时间源</b>：本服务的 <see cref="PeriodicTimer"/> 走系统时钟，
/// 决定「多久检查一次」；任务的触发时刻由 <see cref="Scheduler"/> 内部注入的
/// <see cref="TimeProvider"/> 判定。因此定时器抖动只影响检查的及时性，
/// 不会累积成触发漂移，也便于测试用假时钟复现。
/// </para>
/// <para>
/// 本服务在宿主中**最后注册**，因而最先停止：关闭期间不再拉起新一轮任务，
/// 正在执行的任务由 <c>stoppingToken</c> 通知退出，超时由
/// <see cref="HostOptions.ShutdownTimeout"/> 兜底。
/// </para>
/// </remarks>
internal sealed class SchedulerHostedService : BackgroundService
{
    private readonly Scheduler _scheduler;
    private readonly TimeSpan _interval;
    private readonly ILogger<SchedulerHostedService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="scheduler">定时任务调度器。</param>
    /// <param name="options">调度配置（轮询间隔）。</param>
    /// <param name="logger">结构化日志。</param>
    public SchedulerHostedService(
        Scheduler scheduler,
        IOptions<SchedulingOptions> options,
        ILogger<SchedulerHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scheduler = scheduler;
        _interval = options.Value.PollInterval;
        _logger = logger;
    }

    /// <summary>按固定间隔执行到期任务，直到宿主要求停止。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 轮询间隔配置为非正值时视为关闭调度：安静地结束，不占用后台线程。
        if (_interval <= TimeSpan.Zero)
        {
            _logger.LogInformation("调度轮询间隔配置为非正值，定时任务调度已关闭。");
            return;
        }

        // 任务清单在调度器构造期就已定型，运行期不会增减，因此空清单可以直接关掉循环。
        int taskCount = _scheduler.Tasks.Count;
        if (taskCount == 0)
        {
            _logger.LogInformation("没有登记任何定时任务，调度循环已关闭。");
            return;
        }

        _logger.LogInformation(
            "定时任务调度器已启动，每 {Interval} 轮询一次，共 {TaskCount} 个任务。",
            _interval,
            taskCount);

        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正在关闭，正常退出；关闭播报由 AnBotLifecycleService 负责。
        }
    }

    /// <summary>执行一轮到期任务，异常隔离：单次失败不应终止整个服务。</summary>
    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executed = await _scheduler.RunDueTasksAsync(cancellationToken).ConfigureAwait(false);
            if (executed > 0)
            {
                _logger.LogDebug("本轮执行了 {Count} 个到期任务。", executed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 任务自身的异常已由调度器隔离并记入历史，走到这里说明是调度器或存储层的问题。
            _logger.LogError(ex, "执行定时任务时发生异常，将在下个周期重试。");
        }
    }
}
