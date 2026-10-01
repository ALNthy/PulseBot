using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Scheduling;

namespace PulseBot.Core.Tests;

/// <summary>
/// 调度器（<see cref="Scheduler"/>）的到期判定、执行与历史记录测试。
/// </summary>
/// <remarks>
/// 时间源是手写的 <see cref="FakeTimeProvider"/>，因此「等到下一个周期」「跳过多个周期」
/// 这类场景都不需要真实等待。所有 Cron 用例如无特别说明都显式使用 UTC，
/// 否则结果会随运行机器的时区漂移。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SchedulerTests
{
    /// <summary>统一的时间原点：调度起点即本时刻。</summary>
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 构造完成后立即驱动：还没走到第一个周期点，因此一次都不该执行。
    /// </summary>
    /// <remarks>
    /// 这是刻意的设计——调度起点锚定在调度器构造时刻（宿主启动时刻），
    /// 与 <see cref="PeriodicTimer"/> 的语义一致；否则进程重启就会立刻触发一轮任务。
    /// </remarks>
    [Fact]
    public async Task 任务未到期时不执行()
    {
        var time = new FakeTimeProvider(Start);
        var task = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        Scheduler scheduler = CreateScheduler(time, [task]);

        Assert.Equal(0, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(0, task.ExecutionCount);
    }

    /// <summary>到期执行一次后，到期时刻前移到下一个周期，同一时刻重复驱动不会重复执行。</summary>
    [Fact]
    public async Task 任务到期后每周期只执行一次()
    {
        var time = new FakeTimeProvider(Start);
        var task = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        Scheduler scheduler = CreateScheduler(time, [task]);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(1, task.ExecutionCount);

        // 宿主每个轮询周期都会调用一次，但时间没再往前走，不该有第二次执行。
        Assert.Equal(0, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(1, task.ExecutionCount);
    }

    /// <summary>
    /// 一次性跳过多个周期（休眠、时钟跳变）时只补执行一次。
    /// </summary>
    /// <remarks>
    /// 下一次到期从**执行结束时刻**重新起算（fixed-delay）而不是从计划时刻起算，
    /// 否则补跑会像滚雪球一样堆积成消息风暴。
    /// </remarks>
    [Fact]
    public async Task 错过多个周期时只补执行一次()
    {
        var time = new FakeTimeProvider(Start);
        var task = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        Scheduler scheduler = CreateScheduler(time, [task]);

        time.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(1, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(1, task.ExecutionCount);

        // 执行时刻是 00:03，下一次到期 = 00:03 + 1 分钟，而不是把欠下的两次补齐。
        Assert.Equal(Start + TimeSpan.FromMinutes(4), scheduler.Tasks[0].NextDueAt);
    }

    /// <summary>Cron 任务在指定时区下按挂钟触发，且未到点的日子里不会被触发。</summary>
    [Fact]
    public async Task Cron任务按指定时区触发()
    {
        var time = new FakeTimeProvider(Start);
        var task = new RecordingTask(ScheduledTaskSchedule.Cron("0 8 * * *", TimeZoneInfo.Utc));
        Scheduler scheduler = CreateScheduler(time, [task]);

        // 起点 00:00，当天 08:00 还没到。
        Assert.Equal(0, await scheduler.RunDueTasksAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromHours(8));
        Assert.Equal(1, await scheduler.RunDueTasksAsync(CancellationToken.None));

        // 当天不会再次触发；要等到次日 08:00。
        time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, await scheduler.RunDueTasksAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(2, task.ExecutionCount);
    }

    /// <summary>单个任务抛异常不影响其余任务，失败原因记入执行历史。</summary>
    [Fact]
    public async Task 任务异常被隔离并记入历史()
    {
        var time = new FakeTimeProvider(Start);
        var failing = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)))
        {
            ExceptionToThrow = new InvalidOperationException("炸了"),
        };
        var healthy = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        Scheduler scheduler = CreateScheduler(time, [failing, healthy]);

        time.Advance(TimeSpan.FromMinutes(1));

        // 失败的任务同样计入「本轮已执行」，健康的任务照常跑完。
        Assert.Equal(2, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(1, healthy.ExecutionCount);

        IReadOnlyList<ScheduledTaskExecution> history =
            await scheduler.GetRecentExecutionsAsync(10, CancellationToken.None);

        ScheduledTaskExecution failure = history.Single(e => e.Outcome == ScheduledTaskOutcome.Failed);
        Assert.Equal("炸了", failure.Error);
        Assert.Contains(history, e => e.Outcome == ScheduledTaskOutcome.Succeeded);
    }

    /// <summary>
    /// 宿主关闭导致任务被取消时，先记一笔「已取消」再让取消信号向上传播。
    /// </summary>
    /// <remarks>
    /// 取消不是任务失败，因此 <see cref="ScheduledTaskExecution.Error"/> 应为空；
    /// 记录本身用未取消的令牌写入，否则这条「因取消而中断」的记录会丢失。
    /// </remarks>
    [Fact]
    public async Task 宿主取消时记入历史并向上传播()
    {
        var time = new FakeTimeProvider(Start);
        using var cts = new CancellationTokenSource();
        var task = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)))
        {
            OnExecute = cts.Cancel,
        };
        Scheduler scheduler = CreateScheduler(time, [task]);

        time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduler.RunDueTasksAsync(cts.Token).AsTask());

        IReadOnlyList<ScheduledTaskExecution> history =
            await scheduler.GetRecentExecutionsAsync(10, CancellationToken.None);

        ScheduledTaskExecution canceled = Assert.Single(history);
        Assert.Equal(ScheduledTaskOutcome.Canceled, canceled.Outcome);
        Assert.Null(canceled.Error);
    }

    /// <summary>语法非法的 Cron 表达式只让该任务被跳过，其余任务与宿主不受影响。</summary>
    [Fact]
    public async Task 非法的Cron表达式只跳过该任务()
    {
        var time = new FakeTimeProvider(Start);
        var broken = new RecordingTask(ScheduledTaskSchedule.Cron("不是表达式"));
        var healthy = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        Scheduler scheduler = CreateScheduler(time, [broken, healthy]);

        // 非法任务根本没进清单。
        Assert.Single(scheduler.Tasks);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(0, broken.ExecutionCount);
        Assert.Equal(1, healthy.ExecutionCount);
    }

    /// <summary>未取得分布式锁时跳过本次触发：不执行、不写历史，但到期时刻照常推进。</summary>
    /// <remarks>
    /// 到期时刻必须推进，否则拿不到锁的实例会在每个轮询周期重试，退化成忙等。
    /// </remarks>
    [Fact]
    public async Task 未取得分布式锁时跳过本次触发()
    {
        var time = new FakeTimeProvider(Start);
        var task = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        var distributedLock = new StubDistributedLock { Grant = false };
        Scheduler scheduler = CreateScheduler(time, [task], distributedLock);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await scheduler.RunDueTasksAsync(CancellationToken.None));
        Assert.Equal(1, distributedLock.RequestCount);
        Assert.Equal(0, task.ExecutionCount);
        Assert.Empty(await scheduler.GetRecentExecutionsAsync(10, CancellationToken.None));

        Assert.Equal(Start + TimeSpan.FromMinutes(2), scheduler.Tasks[0].NextDueAt);
        Assert.Equal(0, await scheduler.RunDueTasksAsync(CancellationToken.None));
    }

    /// <summary>执行历史按容量裁剪，只保留最近的若干条，且最新的排在最前。</summary>
    [Fact]
    public async Task 执行历史按容量裁剪并保留最新记录()
    {
        var time = new FakeTimeProvider(Start);
        var task = new RecordingTask(ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));
        Scheduler scheduler = CreateScheduler(time, [task], historyCapacity: 3);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await scheduler.RunDueTasksAsync(CancellationToken.None);
        }

        IReadOnlyList<ScheduledTaskExecution> history =
            await scheduler.GetRecentExecutionsAsync(10, CancellationToken.None);

        Assert.Equal(3, history.Count);
        Assert.Equal(Start + TimeSpan.FromMinutes(5), history[0].ScheduledAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(3), history[^1].ScheduledAt);
    }

    // ---------- 测试基础设施 ----------

    /// <summary>按测试需要装配调度器：默认用真实的内存历史存储与「永远授予」的内存锁。</summary>
    private static Scheduler CreateScheduler(
        TimeProvider timeProvider,
        IScheduledTask[] tasks,
        IDistributedLock? distributedLock = null,
        int historyCapacity = 200)
    {
        IOptions<SchedulingOptions> options = Options.Create(new SchedulingOptions
        {
            HistoryCapacity = historyCapacity,
        });

        return new Scheduler(
            tasks,
            new InMemoryScheduledTaskHistoryStore(options),
            distributedLock ?? new InMemoryDistributedLock(),
            options,
            timeProvider,
            NullLogger<Scheduler>.Instance);
    }

    /// <summary>记录执行次数、可注入异常与副作用的任务替身。</summary>
    private sealed class RecordingTask(ScheduledTaskSchedule schedule) : IScheduledTask
    {
        public ScheduledTaskSchedule Schedule { get; } = schedule;

        public int ExecutionCount { get; private set; }

        /// <summary>非空时在执行时抛出，用于验证异常隔离。</summary>
        public Exception? ExceptionToThrow { get; init; }

        /// <summary>执行时先调用的副作用，用于模拟「执行期间宿主开始关闭」。</summary>
        public Action? OnExecute { get; init; }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            ExecutionCount++;
            OnExecute?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>可切换「授予 / 拒绝」的分布式锁替身。</summary>
    private sealed class StubDistributedLock : IDistributedLock
    {
        /// <summary>为 <c>false</c> 时表示锁已被其它实例占用。</summary>
        public bool Grant { get; init; } = true;

        public int RequestCount { get; private set; }

        public ValueTask<IAsyncDisposable?> TryAcquireAsync(
            string key,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            return ValueTask.FromResult<IAsyncDisposable?>(Grant ? new Handle() : null);
        }

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>可控时间源：只覆写 <see cref="GetUtcNow"/>，用 <see cref="Advance"/> 推进时间。</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
