namespace AnBot.Core.Scheduling;

/// <summary>
/// 分布式锁：多实例部署时，保证同一个定时任务在同一时刻只被一个实例执行。
/// </summary>
/// <remarks>
/// <para>
/// <b>粒度与语义</b>：以「任务」为单位、在任务执行期间持有。取得锁的实例执行任务，
/// 未取得的实例**跳过本次触发**（不排队、不补跑），下一次到期时刻照常推进。
/// </para>
/// <para>
/// <b>默认实现是单机的</b>：<see cref="InMemoryDistributedLock"/> 永远授予锁，
/// 因为单实例下的互斥已由 <see cref="Scheduler"/> 的「单驱动者 + 串行执行」模型保证。
/// 它**不是进程内锁**，不要指望它阻止同一进程内的并发访问。
/// </para>
/// <para>
/// <b>租约</b>：锁有租约时长（<see cref="SchedulingOptions.LockLease"/>），到期自动失效——
/// 否则实例崩溃后任务会被永远锁死。代价是任务执行时间超过租约时另一个实例可能重复执行；
/// 本契约不提供续期，长任务请自行缩短单次粒度。
/// </para>
/// </remarks>
public interface IDistributedLock
{
    /// <summary>尝试获取锁。</summary>
    /// <param name="key">锁键；调度器传入任务标识（任务类型全名）。</param>
    /// <param name="lease">租约时长；超过这段时间后锁自动失效。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    /// <returns>
    /// 获取成功返回释放句柄（释放即解锁，配合 <c>await using</c> 使用）；
    /// 未获取到返回 <c>null</c>。本方法**不阻塞等待**，拿不到立即返回。
    /// </returns>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken);
}
