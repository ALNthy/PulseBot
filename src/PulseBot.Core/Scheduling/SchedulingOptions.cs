namespace PulseBot.Core.Scheduling;

/// <summary>
/// 定时任务调度配置。宿主绑定配置节后通过 <c>IOptions&lt;SchedulingOptions&gt;</c> 注入；
/// 未绑定时使用本类默认值。
/// </summary>
/// <remarks>
/// <para>宿主的推荐用法（appsettings.json）：</para>
/// <code>
/// {
///   "Scheduling": {
///     "PollInterval": "00:00:01",
///     "HistoryCapacity": 200,
///     "LockLease": "00:10:00"
///   }
/// }
/// </code>
/// <para>
/// 与 <see cref="Sessions.SessionOptions"/> 一致：配置写错不应让机器人启动失败，
/// 因此「关闭某项能力」统一用非正值表达，而不是校验抛异常。
/// </para>
/// </remarks>
public sealed class SchedulingOptions
{
    /// <summary>推荐配置节名，宿主形如 <c>services.Configure&lt;SchedulingOptions&gt;(config.GetSection(SchedulingOptions.SectionName))</c>。</summary>
    public const string SectionName = "Scheduling";

    /// <summary>
    /// 调度器轮询间隔，默认 1 秒：宿主每隔这段时间检查一次有没有任务到期。
    /// 设为非正值则整个调度循环关闭（宿主服务安静退出，任何任务都不会执行）。
    /// </summary>
    /// <remarks>
    /// 本项只决定「多久检查一次」，不决定任务的实际触发时刻——到期判定由
    /// <see cref="TimeProvider"/> 完成，因此轮询间隔的抖动不会累积成触发漂移。
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 每个任务保留的最近执行记录条数，默认 200。设为非正值则不保留历史
    /// （记录仍会写日志，只是查询接口返回空）。
    /// </summary>
    public int HistoryCapacity { get; set; } = 200;

    /// <summary>
    /// 分布式锁的租约时长，默认 10 分钟：多实例部署时，取得锁的实例在这段时间内独占该任务。
    /// 应**明显大于**任务的最长执行时间，否则长任务执行到一半租约过期，另一个实例会重复执行。
    /// </summary>
    /// <remarks>
    /// <see cref="IDistributedLock"/> 的默认实现不做租约续期，任务跑得比租约还久就会发生上述重入。
    /// 需要长时间运行的任务请缩短单次粒度，或换用带续期的锁实现。
    /// </remarks>
    public TimeSpan LockLease { get; set; } = TimeSpan.FromMinutes(10);
}
