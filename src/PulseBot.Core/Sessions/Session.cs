using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PulseBot.Core.Sessions;

/// <summary>
/// 一份交互会话：同一会话中同一用户的全部跨消息状态。
/// </summary>
/// <remarks>
/// <para>
/// 会话由 <see cref="ISessionStore"/> 创建与回收，插件只通过存储拿到它，不自行构造
/// （测试除外——构造函数公开就是为了让测试能独立驱动会话行为）。
/// </para>
/// <para>
/// 线程安全：键值状态用 <see cref="ConcurrentDictionary{TKey,TValue}"/>，
/// 只保证单键读写的原子性，**不提供跨键事务**；等待态用 <see cref="Interlocked"/>
/// 完成原子的「检查—取走」；活动时间用 <see cref="Interlocked"/> 读写 UTC 刻度，
/// 而不直接存 <see cref="DateTimeOffset"/>——后者 16 字节，并发读写可能被撕裂。
/// </para>
/// <para>
/// 本类型是普通 class 而非 record，也不要为它实现值相等：
/// 存储清理时用 <c>TryRemove(KeyValuePair)</c> 按**引用相等**删除，以避免误删并发重建出的新会话。
/// </para>
/// <para>
/// 硬约束：本命名空间不得依赖 <c>IMessageSender</c> / <c>IAdapter</c> / <c>IEventPublisher</c>。
/// 那条路径会经「适配器 → 事件入口 → 分发器 → 事件处理器 → 会话存储」绕回自身，
/// 形成容器无法解析的构造期循环依赖。
/// </para>
/// </remarks>
public sealed class Session
{
    /// <summary>跨消息的键值状态。值以 <see cref="object"/> 承载，读取时按请求的类型判定。</summary>
    private readonly ConcurrentDictionary<string, object?> _state = new(StringComparer.Ordinal);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _promptTimeout;

    /// <summary>当前等待中的提问；无等待时为 <c>null</c>。只用 <see cref="Interlocked"/> 访问。</summary>
    private PendingPrompt? _pendingPrompt;

    /// <summary>最近一次被使用的 UTC 刻度。用 long 承载以保证读写的原子性。</summary>
    private long _lastActiveUtcTicks;

    /// <summary>
    /// 创建会话。正常路径由 <see cref="ISessionStore"/> 的实现调用；测试可直接构造。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="timeProvider">时间源。这是全仓唯一的时间来源，禁止改用 <c>DateTime.UtcNow</c>。</param>
    /// <param name="promptTimeout">单次提问的默认等待超时；非正值表示不超时。</param>
    public Session(SessionKey key, TimeProvider timeProvider, TimeSpan promptTimeout)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Key = key;
        _timeProvider = timeProvider;
        _promptTimeout = promptTimeout;

        var now = timeProvider.GetUtcNow();
        CreatedAt = now;
        _lastActiveUtcTicks = now.UtcTicks;
    }

    /// <summary>会话键（会话 + 用户）。</summary>
    public SessionKey Key { get; }

    /// <summary>会话创建时刻。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>最近一次被使用的时刻（读写状态、发起提问、收到回答都会刷新）。</summary>
    public DateTimeOffset LastActiveAt
        => new(Interlocked.Read(ref _lastActiveUtcTicks), TimeSpan.Zero);

    /// <summary>
    /// 登记一次等待：用户在这个会话里的下一条消息会被交给 <paramref name="handler"/> 处理。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本方法是同步的、不做任何 I/O——「把问句发出去」由插件自己用
    /// <c>CommandContext.ReplyAsync</c> 完成。会话层刻意不依赖发送能力，
    /// 否则会绕回事件分发器形成循环依赖（见类型级说明）。
    /// </para>
    /// <para>
    /// 同一会话同时只保留一次等待：重复调用会用新的提问覆盖旧的，旧回调不会被执行。
    /// </para>
    /// </remarks>
    /// <param name="owner">发起提问的插件标识，仅用于日志与诊断（如 <c>"Echo.注册"</c>）。</param>
    /// <param name="handler">收到回答时调用的回调。</param>
    public void BeginPrompt(string owner, SessionReplyHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(handler);

        var expiresAt = _promptTimeout > TimeSpan.Zero
            ? _timeProvider.GetUtcNow() + _promptTimeout
            : DateTimeOffset.MaxValue;

        Volatile.Write(ref _pendingPrompt, new PendingPrompt(owner, handler, expiresAt));
        Touch();
    }

    /// <summary>
    /// 读取键值状态。
    /// </summary>
    /// <typeparam name="T">期望的类型。</typeparam>
    /// <param name="key">状态键。</param>
    /// <param name="value">读到的值；键不存在或类型不匹配时为 <c>default</c>。</param>
    /// <returns>成功读到指定类型的值返回 <c>true</c>。</returns>
    /// <remarks>
    /// 类型不匹配返回 <c>false</c> 而不抛异常：同一个键可能被新旧版本的同名命令先后写入，
    /// 插件读到自己不认识的内容时，当作「没有这个状态」处理更稳妥。
    /// </remarks>
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_state.TryGetValue(key, out object? stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>写入键值状态，覆盖同名旧值。</summary>
    /// <typeparam name="T">值的类型；读取时需用同一类型。</typeparam>
    /// <param name="key">状态键。</param>
    /// <param name="value">要保存的值。</param>
    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _state[key] = value;
        Touch();
    }

    /// <summary>移除键值状态。</summary>
    /// <param name="key">状态键。</param>
    /// <returns>确实移除了返回 <c>true</c>；键不存在返回 <c>false</c>。</returns>
    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _state.TryRemove(key, out _);
    }

    /// <summary>
    /// 原子地取走当前等待态（若尚未超时）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「检查」与「取走」必须是同一个原子步骤：两条消息并发到达同一个会话时，
    /// 若先读后取，回调会被触发两次。
    /// </para>
    /// <para>
    /// 返回 <c>false</c> 有两种情况——本来就没有等待态，或等待态已超时（超时的会被清除）。
    /// 两者都意味着消息应当继续流向其它处理器，而不是被当作迟到的回答吞掉。
    /// </para>
    /// </remarks>
    /// <param name="prompt">取走的等待态；返回 <c>false</c> 时为 <c>null</c>。</param>
    internal bool TryConsumePrompt([NotNullWhen(true)] out PendingPrompt? prompt)
    {
        PendingPrompt? consumed = Interlocked.Exchange(ref _pendingPrompt, null);

        if (consumed is null || _timeProvider.GetUtcNow() >= consumed.ExpiresAt)
        {
            prompt = null;
            return false;
        }

        prompt = consumed;
        Touch();
        return true;
    }

    /// <summary>
    /// 刷新活动时间。
    /// </summary>
    /// <remarks>
    /// 只有「真正使用了会话」的路径才调用它：<see cref="Set{T}"/>、<see cref="BeginPrompt"/>
    /// 与成功取走等待态。普通消息的查找（<see cref="ISessionStore.FindAsync"/>）不刷新，
    /// 因此会话的生命周期由最后使用时刻决定，而不是最后发言时刻。
    /// </remarks>
    internal void Touch()
        => Interlocked.Exchange(ref _lastActiveUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
}
