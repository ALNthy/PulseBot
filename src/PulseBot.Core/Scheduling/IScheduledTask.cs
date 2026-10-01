namespace PulseBot.Core.Scheduling;

/// <summary>
/// 定时任务契约：由 <see cref="Scheduler"/> 统一编排执行。
/// </summary>
/// <remarks>
/// <para>
/// 任务与插件（<see cref="Plugins.IPlugin"/>）是**并列**的两个概念，不互相嵌套：
/// 插件是「有加载/卸载钩子、注册命令」的单元，而定时任务启动即参与调度、没有任何生命周期钩子。
/// 插件项目需要定时能力时，只需在自己的 <c>AddXxxPlugin()</c> 里调用
/// <c>AddScheduledTask&lt;MyTask&gt;()</c>。
/// </para>
/// <para>
/// <b>任务标识</b>：本接口不提供 Id 成员。调度器取任务类型的全名作为标识
/// （注册用 <c>TryAddEnumerable</c>，同一类型不可能注册两次，类型名天然唯一稳定），
/// 它同时用作日志字段、执行历史的 <see cref="ScheduledTaskExecution.TaskId"/> 与分布式锁的键。
/// </para>
/// <para>
/// <b>依赖获取</b>：任务由容器托管，所需服务一律构造函数注入（需要当前时间就注入
/// <see cref="TimeProvider"/>）；调度器不会传入上下文对象。
/// </para>
/// <para>
/// <b>异常与取消</b>：<see cref="ExecuteAsync"/> 抛出的异常由调度器捕获并记录
/// （记入执行历史后继续执行其它任务，不影响宿主），但 <paramref name="cancellationToken"/>
/// 已取消时的 <see cref="OperationCanceledException"/> 会正常向上传播——那是宿主关闭，不是任务失败。
/// </para>
/// </remarks>
public interface IScheduledTask
{
    /// <summary>触发方式：固定间隔或 Cron 表达式，二选一。</summary>
    ScheduledTaskSchedule Schedule { get; }

    /// <summary>执行一次任务。</summary>
    /// <param name="cancellationToken">宿主关闭时触发；任务应尽快返回。</param>
    ValueTask ExecuteAsync(CancellationToken cancellationToken);
}
