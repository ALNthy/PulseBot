namespace PulseBot.Core.Sessions;

/// <summary>
/// 交互会话的存储：按 <see cref="SessionKey"/> 存取 <see cref="Session"/>。
/// </summary>
/// <remarks>
/// <para>
/// 默认实现 <see cref="InMemorySessionStore"/> 把会话保存在进程内存中，进程重启即全部丢失。
/// 这是有意的：会话是短生命周期的交互状态，不是需要长期保留的数据。
/// </para>
/// <para>
/// <b>关于持久化实现</b>：本接口目前无法直接换成 Redis 等外部存储——
/// <see cref="Session"/> 的等待态持有插件回调委托，不可序列化。
/// 真要持久化，需要先把等待态改造成「插件标识 + 可序列化载荷」，
/// 并为插件侧新增一套处理器注册表，这是接口签名的破坏性变更，等确有需求时再做。
/// </para>
/// <para>
/// 实现必须线程安全：同一个会话可能被并发访问。
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// 查找会话，**不存在时返回 <c>null</c> 而不创建**。
    /// 事件分发的热路径（每条消息都要跑）必须用本方法：拿不到会话实例就一定没有等待中的提问。
    /// </summary>
    /// <remarks>
    /// 查找是纯读操作，**不刷新活动时间**——会话的生命周期由「最后一次真正使用」
    /// （读写状态、发起提问、收到回答）决定，而不是「最后一次发言」。
    /// </remarks>
    /// <param name="key">会话键。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    ValueTask<Session?> FindAsync(SessionKey key, CancellationToken cancellationToken);

    /// <summary>
    /// 取得会话，不存在则创建；已空闲超时的会话会被丢弃后重新创建。
    /// 插件在需要读写状态或发起提问时调用。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    ValueTask<Session> GetOrCreateAsync(SessionKey key, CancellationToken cancellationToken);

    /// <summary>移除会话及其全部状态。</summary>
    /// <param name="key">会话键。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    /// <returns>确实移除了返回 <c>true</c>；会话本来就不存在返回 <c>false</c>。</returns>
    ValueTask<bool> RemoveAsync(SessionKey key, CancellationToken cancellationToken);

    /// <summary>
    /// 清理所有空闲超时的会话并返回移除数量。
    /// </summary>
    /// <remarks>
    /// Core 不引用 Hosting 抽象，因此这里不做后台服务；由宿主（如 Cli 的
    /// <c>SessionCleanupService</c>）按 <see cref="SessionOptions.CleanupInterval"/> 定时驱动。
    /// 即便从不调用本方法，过期会话也不会被读到脏状态——下次访问时会被懒丢弃；
    /// 不清理的唯一后果是「再也不说话的用户」的会话对象长期占用内存。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    ValueTask<int> CleanupExpiredAsync(CancellationToken cancellationToken);
}
