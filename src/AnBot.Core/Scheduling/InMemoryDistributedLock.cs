namespace AnBot.Core.Scheduling;

/// <summary>
/// <see cref="IDistributedLock"/> 的单机默认实现：**永远授予**锁。
/// </summary>
/// <remarks>
/// <para>
/// 单实例部署下，同一个任务的并发执行已被 <see cref="Scheduler"/> 的
/// 「单驱动者 + 串行执行」模型排除，因此这里不需要真实互斥——永远发放同一张通行证。
/// </para>
/// <para>
/// <b>它不是进程内锁</b>：同一进程里两个调用者同时获取同一个键都会成功。
/// 多实例部署时请用带真实互斥的实现（Redis / 数据库）覆盖它：
/// <c>services.AddSingleton&lt;IDistributedLock, RedisDistributedLock&gt;()</c>。
/// </para>
/// </remarks>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    /// <summary>空句柄可复用；解锁无需任何动作。</summary>
    private static readonly IAsyncDisposable Handle = new NoopHandle();

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string key,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IAsyncDisposable?>(Handle);
    }

    /// <summary>解锁时无事可做的句柄。</summary>
    private sealed class NoopHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
