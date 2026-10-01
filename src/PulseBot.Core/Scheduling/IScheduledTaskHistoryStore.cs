namespace PulseBot.Core.Scheduling;

/// <summary>
/// 执行历史的存储：调度器写入，后台管理与诊断读取。
/// </summary>
/// <remarks>
/// <para>
/// 默认实现 <see cref="InMemoryScheduledTaskHistoryStore"/> 把记录保存在进程内存中，
/// 进程重启即丢失。这是有意的：本轮定位是「让调度器自带可查询的执行历史」，
/// 真正的落库（EF Core / Redis）属于后台管理层。
/// </para>
/// <para>
/// <b>与 <see cref="Sessions.ISessionStore"/> 的关键差别</b>：本接口的记录是纯可序列化数据
/// （见 <see cref="ScheduledTaskExecution"/> 的说明），换成持久化实现**不需要破坏性改造**——
/// 宿主用 <c>services.AddSingleton&lt;IScheduledTaskHistoryStore, MyStore&gt;()</c> 覆盖即可。
/// </para>
/// <para>实现必须线程安全：调度器写入，后台界面并发读取。</para>
/// </remarks>
public interface IScheduledTaskHistoryStore
{
    /// <summary>记录一次执行结果。</summary>
    /// <param name="execution">执行记录。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    ValueTask RecordAsync(ScheduledTaskExecution execution, CancellationToken cancellationToken);

    /// <summary>读取最近若干条执行记录，最新的在前。</summary>
    /// <param name="maxCount">最多返回多少条；非正值表示不限（由实现自行决定上限）。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    /// <returns>执行记录快照。</returns>
    ValueTask<IReadOnlyList<ScheduledTaskExecution>> GetRecentAsync(int maxCount, CancellationToken cancellationToken);
}
