namespace AnBot.Core.Scheduling;

/// <summary>一次定时任务执行的结局。</summary>
public enum ScheduledTaskOutcome
{
    /// <summary>正常完成。</summary>
    Succeeded = 0,

    /// <summary>执行时抛出异常。异常已被调度器记录，不影响其它任务，也不会导致宿主崩溃。</summary>
    Failed = 1,

    /// <summary>执行期间宿主正在关闭，任务被取消。</summary>
    Canceled = 2,
}

/// <summary>
/// 一条执行历史记录：供后台管理查看「任务什么时候跑过、跑了多久、成没成」。
/// </summary>
/// <remarks>
/// <para>
/// 记录里的字段全部是基元类型（字符串、时间、枚举），**可直接序列化**——
/// 这正是存储契约 <see cref="IScheduledTaskHistoryStore"/> 能无痛换成
/// EF Core / Redis 实现的原因：改为持久化实现不需要改动本类型。
/// </para>
/// <para>
/// <see cref="Error"/> 只保存异常的 <c>Message</c>：堆栈属于诊断细节，只进结构化日志。
/// 历史记录可能被后台界面直接展示给运营人员，不宜携带堆栈。
/// </para>
/// </remarks>
/// <param name="TaskId">任务标识（任务类型的全名）。</param>
/// <param name="ScheduledAt">计划触发时刻；可能明显早于实际开始时刻（进程刚启动或任务排队时）。</param>
/// <param name="StartedAt">实际开始执行的时刻。</param>
/// <param name="Duration">本次执行耗时。</param>
/// <param name="Outcome">执行结局。</param>
/// <param name="Error">失败时的异常信息；成功与取消时为 <c>null</c>。</param>
public sealed record ScheduledTaskExecution(
    string TaskId,
    DateTimeOffset ScheduledAt,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    ScheduledTaskOutcome Outcome,
    string? Error = null);
