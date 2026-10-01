using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PulseBot.Core.Sessions;

/// <summary>
/// <see cref="ISessionStore"/> 的默认实现：会话保存在进程内存中，进程重启即全部丢失。
/// </summary>
/// <remarks>
/// <para>
/// 线程安全：会话表用 <see cref="ConcurrentDictionary{TKey,TValue}"/>，读路径无锁；
/// 「取值 → 判断过期 → 重建」以乐观重试完成，与 <c>InMemoryPermissionService</c>
/// 处理授权表的思路一致。
/// </para>
/// <para>
/// 过期会话一律用 <c>TryRemove(KeyValuePair)</c> 做**条件删除**：只有字典里仍是同一个实例时才移除，
/// 避免误删并发重建出的新会话。这也是 <see cref="Session"/> 不能实现值相等的原因。
/// </para>
/// </remarks>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<SessionKey, Session> _sessions = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _promptTimeout;
    private readonly ILogger<InMemorySessionStore> _logger;

    /// <summary>创建内存会话存储。</summary>
    /// <param name="options">会话配置（空闲超时、提问超时）。</param>
    /// <param name="timeProvider">时间源。这是全仓唯一的时间来源，禁止改用 <c>DateTime.UtcNow</c>。</param>
    /// <param name="logger">结构化日志。</param>
    public InMemorySessionStore(
        IOptions<SessionOptions> options,
        TimeProvider timeProvider,
        ILogger<InMemorySessionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;

        _timeProvider = timeProvider;
        _idleTimeout = value.IdleTimeout;
        _promptTimeout = value.PromptTimeout;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<Session?> FindAsync(SessionKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        // 纯读：不创建、不刷新活动时间。热路径上只有一次哈希查找。
        _sessions.TryGetValue(key, out Session? session);
        return ValueTask.FromResult(session);
    }

    /// <inheritdoc />
    public ValueTask<Session> GetOrCreateAsync(SessionKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        // 乐观重试：「命中但已过期」要先移除再重建，其间可能有别的线程抢先建好，因此循环。
        while (true)
        {
            if (_sessions.TryGetValue(key, out Session? existing))
            {
                if (!IsIdleExpired(existing))
                {
                    existing.Touch();
                    return ValueTask.FromResult(existing);
                }

                // 条件删除：只有当字典里仍是刚才那个过期实例时才移除。
                // 若别的线程已抢先替换，这次删除是空操作，下一轮循环会取到新实例。
                _sessions.TryRemove(new KeyValuePair<SessionKey, Session>(key, existing));
                continue;
            }

            var created = new Session(key, _timeProvider, _promptTimeout);
            if (_sessions.TryAdd(key, created))
            {
                return ValueTask.FromResult(created);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(SessionKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        return ValueTask.FromResult(_sessions.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public ValueTask<int> CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        // 关闭过期策略时无事可做，连遍历都省掉。
        if (_idleTimeout <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(0);
        }

        var removed = 0;

        // 遍历的是并发字典的快照视图，期间的增删不会破坏遍历。
        foreach (var entry in _sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsIdleExpired(entry.Value) && _sessions.TryRemove(entry))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug("已清理 {Count} 个空闲超时的交互会话。", removed);
        }

        return ValueTask.FromResult(removed);
    }

    /// <summary>判断会话是否已空闲超时；空闲超时为非正值时表示永不过期。</summary>
    private bool IsIdleExpired(Session session)
        => _idleTimeout > TimeSpan.Zero
            && _timeProvider.GetUtcNow() - session.LastActiveAt >= _idleTimeout;
}
