namespace AnBot.Core.Scheduling;

/// <summary>
/// 任务清单的只读快照：供日志、诊断与后台管理展示。
/// </summary>
/// <remarks>
/// 与 <see cref="Commands.CommandDescriptor"/> 同一定位——仅供展示，不要在热路径上使用。
/// <see cref="NextDueAt"/> 由调度循环写入、可能被其它线程并发读取，而
/// <see cref="DateTimeOffset"/> 不是原子类型，因此读到的可能是上一次的值；
/// 诊断用途不追求精确，与 <see cref="Plugins.PluginManager.LoadedPlugins"/> 的处理方式一致。
/// </remarks>
/// <param name="TaskId">任务标识（任务类型的全名）。</param>
/// <param name="Schedule">触发方式（固定间隔或 Cron）。</param>
/// <param name="NextDueAt">
/// 下一次计划触发时刻；已停摆的任务为 <see cref="DateTimeOffset.MaxValue"/>
/// （例如 Cron 表达式永远不可能匹配到任何时刻）。
/// </param>
public sealed record ScheduledTaskDescriptor(
    string TaskId,
    ScheduledTaskSchedule Schedule,
    DateTimeOffset NextDueAt);
