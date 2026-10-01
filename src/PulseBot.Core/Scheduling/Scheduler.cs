using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PulseBot.Core.Scheduling;

/// <summary>
/// 定时任务调度器：按 <see cref="IScheduledTask.Schedule"/> 把到期任务跑起来，并记录执行历史。
/// </summary>
/// <remarks>
/// <para>
/// <b>由宿主驱动，Core 不自己起定时器</b>——与 <see cref="Sessions.ISessionStore.CleanupExpiredAsync"/>
/// 同一分工（Core 不引用 Hosting 抽象）：宿主每隔 <see cref="SchedulingOptions.PollInterval"/>
/// 调用一次 <see cref="RunDueTasksAsync"/>，每次调用把所有到期任务执行一轮后返回。
/// </para>
/// <para>
/// <b>触发时刻的锚定</b>：调度起点是**本对象被构造的时刻**（即宿主启动时刻），
/// 首次执行落在第一个周期点或下一个 Cron 时刻，**不会一启动就执行一次**。
/// 进程重启会让固定间隔任务的相位重置，而 Cron 任务按挂钟计算、不受重启影响——
/// 因此需要「每天 8:00 准时」这类稳定时刻的任务应当用 Cron 而不是间隔。
/// </para>
/// <para>
/// <b>错过与补偿</b>：下一次到期时刻在任务**执行结束之后**才重算。因此进程休眠数小时后唤醒，
/// 每个任务最多补执行一次（不是每小时补一次），不会形成消息风暴。
/// </para>
/// <para>
/// <b>执行模型</b>：任务**串行**执行——一个任务跑完才轮到下一个。这是有意的取舍：
/// 并行会让「同一个任务上一次还没跑完、下一次又到期」成为可能，从而需要重入保护、
/// 并发取消等一整套机制；而机器人侧的定时任务通常只有秒级耗时。
/// 代价是单个慢任务会推迟本轮其它任务，请勿在任务里做长时间阻塞操作。
/// </para>
/// <para>
/// <b>单驱动者约定</b>：<see cref="RunDueTasksAsync"/> 只允许由一个调用者（宿主的调度循环）
/// 驱动。若将来要加「后台手动触发一次」这类能力，必须把请求排队进同一个循环，
/// 否则两个调用者会同时执行同一个任务。
/// </para>
/// </remarks>
public sealed class Scheduler
{
    /// <summary>分布式锁键前缀：多实例共用同一存储时，不同应用之间不会互相干扰。</summary>
    private const string LockKeyPrefix = "pulsebot:schedule:";

    /// <summary>已编译的任务清单：构造期定型，运行期只读（仅 <see cref="TaskRegistration.NextDueAt"/> 可变）。</summary>
    private readonly TaskRegistration[] _tasks;

    private readonly IScheduledTaskHistoryStore _history;
    private readonly IDistributedLock _distributedLock;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lockLease;
    private readonly ILogger<Scheduler> _logger;

    /// <summary>创建调度器，并把每个任务的触发方式编译为运行期可直接调用的委托。</summary>
    /// <param name="tasks">容器中登记的全部定时任务。</param>
    /// <param name="history">执行历史存储。</param>
    /// <param name="distributedLock">分布式锁；单实例下用默认的内存实现即可。</param>
    /// <param name="options">调度配置（轮询间隔、历史容量、锁租约）。</param>
    /// <param name="timeProvider">时间源。这是到期判定的唯一依据，禁止改用 <c>DateTime.UtcNow</c>。</param>
    /// <param name="logger">结构化日志。</param>
    public Scheduler(
        IEnumerable<IScheduledTask> tasks,
        IScheduledTaskHistoryStore history,
        IDistributedLock distributedLock,
        IOptions<SchedulingOptions> options,
        TimeProvider timeProvider,
        ILogger<Scheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(distributedLock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _history = history;
        _distributedLock = distributedLock;
        _timeProvider = timeProvider;
        _lockLease = options.Value.LockLease;
        _logger = logger;

        // 构造期一次性「编译」：解析 Cron 表达式、缓存计算下一次到期的委托，并以当前时刻锚定首次到期。
        // 运行期的到期循环因此不含任何解析、反射或类型判断。
        _tasks = BuildRegistrations(tasks, timeProvider.GetUtcNow(), logger);
    }

    /// <summary>
    /// 任务清单快照（含各自的下一次到期时刻），供日志、诊断与后台管理展示。
    /// </summary>
    /// <remarks>
    /// 仅供展示，不要在热路径上调用——每次访问都会新建一个数组。
    /// </remarks>
    public IReadOnlyList<ScheduledTaskDescriptor> Tasks
    {
        get
        {
            var snapshot = new ScheduledTaskDescriptor[_tasks.Length];

            for (var i = 0; i < _tasks.Length; i++)
            {
                TaskRegistration registration = _tasks[i];
                snapshot[i] = new ScheduledTaskDescriptor(
                    registration.TaskId,
                    registration.Task.Schedule,
                    registration.NextDueAt);
            }

            return snapshot;
        }
    }

    /// <summary>
    /// 执行一轮：把所有已到期的任务依次跑完，返回本轮真正执行的任务数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 宿主每隔 <see cref="SchedulingOptions.PollInterval"/> 调用一次本方法；未到期的任务会被
    /// 直接跳过，因此重复调用是廉价的。只允许一个驱动者，理由见类型级说明。
    /// </para>
    /// <para>
    /// 单个任务抛出的异常会被捕获并记录（记入执行历史后继续下一个任务），不会向外传播；
    /// 只有宿主关闭（<paramref name="cancellationToken"/> 已取消）时的取消信号会正常向上传播。
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">宿主关闭时触发。</param>
    /// <returns>本轮真正执行了的任务数（未到期、或因拿不到分布式锁而跳过的都不计入）。</returns>
    public async ValueTask<int> RunDueTasksAsync(CancellationToken cancellationToken)
    {
        var executed = 0;

        foreach (TaskRegistration registration in _tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset dueAt = registration.NextDueAt;

            // 每个任务各自读一次当前时间：先执行的任务若耗时较长，不会让后面任务的到期判定失真。
            if (_timeProvider.GetUtcNow() < dueAt)
            {
                continue;
            }

            if (await RunAsync(registration, dueAt, cancellationToken).ConfigureAwait(false))
            {
                executed++;
            }

            // 无论是否真的执行（可能被别的实例抢先），到期时刻都要推进，否则会每个轮询周期重试一次。
            // 用「执行结束后的当前时间」重算，于是错过多次也只补一次。
            registration.NextDueAt = registration.ComputeNext(_timeProvider.GetUtcNow())
                ?? DateTimeOffset.MaxValue;
        }

        return executed;
    }

    /// <summary>读取最近的执行记录，最新的在前。</summary>
    /// <param name="maxCount">最多返回多少条；非正值表示不限。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    public ValueTask<IReadOnlyList<ScheduledTaskExecution>> GetRecentExecutionsAsync(
        int maxCount,
        CancellationToken cancellationToken)
        => _history.GetRecentAsync(maxCount, cancellationToken);

    /// <summary>把任务清单编译为运行期结构；触发方式非法的任务会被记录并跳过，不影响其余任务。</summary>
    private static TaskRegistration[] BuildRegistrations(
        IEnumerable<IScheduledTask> tasks,
        DateTimeOffset now,
        ILogger logger)
    {
        // 物化一次，避免每次枚举时重新解析容器。
        IReadOnlyList<IScheduledTask> materialized = tasks as IReadOnlyList<IScheduledTask> ?? tasks.ToArray();
        var registrations = new List<TaskRegistration>(materialized.Count);

        foreach (IScheduledTask task in materialized)
        {
            var taskType = task.GetType();
            var taskId = taskType.FullName ?? taskType.Name;

            Func<DateTimeOffset, DateTimeOffset?>? computeNext = TryCompile(task.Schedule, taskId, logger);

            if (computeNext is null)
            {
                // 表达式非法：已记录原因，跳过这个任务，其余任务与宿主不受影响。
                continue;
            }

            registrations.Add(new TaskRegistration(
                task,
                taskId,
                computeNext,
                computeNext(now) ?? DateTimeOffset.MaxValue));
        }

        return registrations.ToArray();
    }

    /// <summary>
    /// 把触发方式编译为「给定时刻 → 下一次到期时刻」的委托；Cron 表达式只在这里解析一次。
    /// </summary>
    /// <returns>编译好的委托；触发方式非法时返回 <c>null</c>（原因已记入日志）。</returns>
    private static Func<DateTimeOffset, DateTimeOffset?>? TryCompile(
        ScheduledTaskSchedule schedule,
        string taskId,
        ILogger logger)
    {
        switch (schedule)
        {
            case ScheduledTaskSchedule.IntervalSchedule interval:
                return now => now + interval.Interval;

            case ScheduledTaskSchedule.CronSchedule cron:
                CronExpression expression;

                try
                {
                    expression = CronExpression.Parse(cron.Expression, DetectCronFormat(cron.Expression));
                }
                catch (CronFormatException ex)
                {
                    logger.LogError(
                        ex,
                        "定时任务 {TaskId} 的 Cron 表达式 {Expression} 语法非法，该任务已被跳过。",
                        taskId,
                        cron.Expression);
                    return null;
                }

                // 返回可空：表达式永远不可能匹配到任何时刻（如 2 月 30 日）时返回 null，
                // 调用方据此把任务停摆，而不是每个轮询周期都触发一次。
                return now => expression.GetNextOccurrence(now, cron.TimeZone);

            default:
                // 封闭联合保证不会走到这里；万一将来新增了触发方式而忘了在这里处理，
                // 报错跳过比静默跑错要好。
                logger.LogError(
                    "定时任务 {TaskId} 使用了调度器不认识的触发方式 {Schedule}，该任务已被跳过。",
                    taskId,
                    schedule.GetType().Name);
                return null;
        }
    }

    /// <summary>
    /// 判断 Cron 表达式是 5 段（分钟粒度）还是 6 段（含秒）。
    /// </summary>
    /// <remarks>
    /// 以空白分隔的字段数推断：6 段及以上按含秒解析，其余（含 <c>@daily</c> 这类单段宏）按标准格式。
    /// </remarks>
    private static CronFormat DetectCronFormat(string expression)
        => expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 5
            ? CronFormat.IncludeSeconds
            : CronFormat.Standard;

    /// <summary>执行单个到期任务并记录历史。</summary>
    /// <returns>确实执行了返回 <c>true</c>；因未取得分布式锁而跳过返回 <c>false</c>。</returns>
    private async ValueTask<bool> RunAsync(
        TaskRegistration registration,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        // 多实例互斥：拿不到锁说明别的实例正在跑，本次触发直接跳过（不排队、不补跑）。
        await using IAsyncDisposable? handle = await _distributedLock
            .TryAcquireAsync(LockKeyPrefix + registration.TaskId, _lockLease, cancellationToken)
            .ConfigureAwait(false);

        if (handle is null)
        {
            _logger.LogDebug(
                "定时任务 {TaskId} 正被其它实例执行，本次触发已跳过。",
                registration.TaskId);
            return false;
        }

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        try
        {
            await registration.Task.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主正在关闭：先记一笔「已取消」，再让取消信号正常向上传播（这是正常控制流，不是任务失败）。
            // 此处用 None 写入历史——记录是收尾动作，不该因为令牌已取消而丢失。
            await RecordAsync(
                registration.TaskId,
                dueAt,
                startedAt,
                ScheduledTaskOutcome.Canceled,
                error: null,
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }
        catch (Exception ex)
        {
            // 异常隔离：记录后继续执行其余任务。重试属于任务自身的职责（等下个自然周期即可）。
            _logger.LogError(ex, "定时任务 {TaskId} 执行失败。", registration.TaskId);

            await RecordAsync(
                registration.TaskId,
                dueAt,
                startedAt,
                ScheduledTaskOutcome.Failed,
                ex.Message,
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        await RecordAsync(
            registration.TaskId,
            dueAt,
            startedAt,
            ScheduledTaskOutcome.Succeeded,
            error: null,
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>写入一条执行历史；耗时以时间源为准，便于测试用假时钟复现。</summary>
    private ValueTask RecordAsync(
        string taskId,
        DateTimeOffset dueAt,
        DateTimeOffset startedAt,
        ScheduledTaskOutcome outcome,
        string? error,
        CancellationToken cancellationToken)
        => _history.RecordAsync(
            new ScheduledTaskExecution(
                taskId,
                dueAt,
                startedAt,
                _timeProvider.GetUtcNow() - startedAt,
                outcome,
                error),
            cancellationToken);

    /// <summary>
    /// 一个已编译的任务：运行期只用到这几个字段，不再触碰 <see cref="IScheduledTask.Schedule"/>。
    /// </summary>
    /// <param name="Task">被调度的任务实例。</param>
    /// <param name="TaskId">任务标识（任务类型全名）。</param>
    /// <param name="ComputeNext">「给定时刻 → 下一次到期时刻」；返回 <c>null</c> 表示不再触发。</param>
    /// <param name="NextDueAt">下一次计划触发时刻；<see cref="DateTimeOffset.MaxValue"/> 表示已停摆。</param>
    private sealed class TaskRegistration(
        IScheduledTask task,
        string taskId,
        Func<DateTimeOffset, DateTimeOffset?> computeNext,
        DateTimeOffset nextDueAt)
    {
        public IScheduledTask Task { get; } = task;

        public string TaskId { get; } = taskId;

        public Func<DateTimeOffset, DateTimeOffset?> ComputeNext { get; } = computeNext;

        public DateTimeOffset NextDueAt { get; set; } = nextDueAt;
    }
}
