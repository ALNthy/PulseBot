using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Shared;

namespace PulseBot.Admin.Infrastructure.Auth;

/// <summary>
/// 登录失败限流：按账号与来源 IP 两个维度独立计数，达到阈值后暂时锁定。
/// </summary>
/// <remarks>
/// 这是**认证服务的内部依赖**，不供接口层直接调用——判定必须发生在服务端最前面，
/// 否则端点可以绕过限流直接调 <see cref="IAuthenticationService.LoginAsync"/>。
/// </remarks>
public interface ILoginThrottleService
{
    /// <summary>查询当前是否处于锁定状态。</summary>
    /// <param name="userName">尝试登录的用户名（不必真实存在）。</param>
    /// <param name="remoteIp">来源 IP；为空表示无法取得，该维度不参与计数。</param>
    /// <param name="retryAfter">被锁定时返回还需等待多久；未锁定时为 <see cref="TimeSpan.Zero"/>。</param>
    /// <returns>任一维度处于锁定状态返回 <c>true</c>。</returns>
    bool TryGetLockout(string userName, string? remoteIp, out TimeSpan retryAfter);

    /// <summary>记录一次登录失败。</summary>
    /// <param name="userName">尝试登录的用户名。</param>
    /// <param name="remoteIp">来源 IP；为空表示无法取得。</param>
    void RecordFailure(string userName, string? remoteIp);

    /// <summary>记录一次登录成功，清除该账号的失败计数。</summary>
    /// <param name="userName">登录成功的用户名。</param>
    void RecordSuccess(string userName);
}

/// <summary>
/// 进程内实现的登录限流。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是锁</b>：限流位于登录请求的
/// 前置路径上，攻击者能用大量并发请求打到这里；全局锁会把限流器本身变成瓶颈。
/// 每个键各自原子更新（<see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate"/>），
/// 不同账号/不同 IP 之间互不阻塞。
/// </para>
/// <para>
/// <b>内存态</b>：重启清零。重启进程需要本机权限，而攻击者一旦拿到本机权限，
/// 限流早已不是主要防线，因此不构成实质绕过。
/// </para>
/// <para>
/// <b>容量</b>：字典的键来自攻击者可控的输入（任意用户名）。这里**不设上限**，
/// 因为 IP 维度的阈值天然限制了单个来源能制造出的键数量，而清理会移除过期条目；
/// 真要防内存耗尽，属于接入层的全局限流职责，不是本类的。
/// </para>
/// </remarks>
public sealed class InMemoryLoginThrottleService : ILoginThrottleService
{
    /// <summary>两次机会性清理之间的最小间隔。</summary>
    private const long CleanupIntervalMs = 60_000;

    /// <summary>键前缀：账号维度。</summary>
    private const string UserPrefix = "u:";

    /// <summary>键前缀：来源 IP 维度。</summary>
    private const string IpPrefix = "i:";

    private readonly ConcurrentDictionary<string, FailureWindow> _windows = new(StringComparer.Ordinal);
    private readonly AdminThrottleOptions _throttle;
    private readonly TimeProvider _time;

    /// <summary>上次清理的时刻（Unix 毫秒）。用 <see cref="Interlocked"/> 读写，因此不加 <c>volatile</c>。</summary>
    private long _lastCleanupMs;

    /// <summary>构造函数注入。</summary>
    /// <param name="options">后台配置（取其中的限流节）。</param>
    /// <param name="time">时间源。</param>
    public InMemoryLoginThrottleService(IOptions<AdminOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _throttle = options.Value.Throttle;
        _time = time;
    }

    /// <inheritdoc />
    public bool TryGetLockout(string userName, string? remoteIp, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan userRemaining = RemainingLockout(UserKey(userName), now);
        TimeSpan ipRemaining = string.IsNullOrWhiteSpace(remoteIp)
            ? TimeSpan.Zero
            : RemainingLockout(IpKey(remoteIp), now);

        // 两个桶任一锁定即拒绝。告知两者中更长的等待时间：只报较短的会让客户端
        // 按它重试后再次被另一个桶拒掉，看起来像限流没有生效。
        retryAfter = userRemaining > ipRemaining ? userRemaining : ipRemaining;

        return retryAfter > TimeSpan.Zero;
    }

    /// <inheritdoc />
    public void RecordFailure(string userName, string? remoteIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        DateTimeOffset now = _time.GetUtcNow();

        AddFailure(UserKey(userName), _throttle.MaxFailuresPerUser, now);

        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            AddFailure(IpKey(remoteIp), _throttle.MaxFailuresPerIp, now);
        }

        CleanupIfDue(now);
    }

    /// <inheritdoc />
    public void RecordSuccess(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        // 只清账号桶，**不清 IP 桶**：一次成功登录不该把同一来源之前对其它账号的
        // 失败尝试一笔勾销——那恰好是「用一个自己的账号反复登录来重置 IP 计数」的绕过手法。
        // IP 桶靠窗口自然过期。
        _windows.TryRemove(UserKey(userName), out _);
    }

    /// <summary>构造账号维度的键。保留大小写：与登录时「用户名精确匹配」的语义保持一致。</summary>
    private static string UserKey(string userName) => UserPrefix + userName.Trim();

    /// <summary>构造来源 IP 维度的键。</summary>
    private static string IpKey(string remoteIp) => IpPrefix + remoteIp.Trim();

    /// <summary>读取某个键剩余的锁定时长，未锁定返回 <see cref="TimeSpan.Zero"/>。</summary>
    private TimeSpan RemainingLockout(string key, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(key, out FailureWindow? window) || window is null)
        {
            return TimeSpan.Zero;
        }

        if (window.LockedUntil is not { } lockedUntil || lockedUntil <= now)
        {
            return TimeSpan.Zero;
        }

        return lockedUntil - now;
    }

    /// <summary>在指定键上累加一次失败。</summary>
    private void AddFailure(string key, int threshold, DateTimeOffset now)
    {
        // 新键从一个空窗口开始推进，与「已有窗口再记一次失败」走同一条 Advance 逻辑，
        // 因此「阈值 = 1 时首次失败即锁定」这类边界不需要单独分支。
        // AddOrUpdate 的委托可能被并发调用多次，故两个委托都只做无副作用计算。
        _windows.AddOrUpdate(
            key,
            _ => Advance(new FailureWindow(Count: 0, WindowStart: now, LockedUntil: null), threshold, now),
            (_, existing) => Advance(existing, threshold, now));
    }

    /// <summary>根据当前窗口状态推进一次失败计数。</summary>
    private FailureWindow Advance(FailureWindow existing, int threshold, DateTimeOffset now)
    {
        // 仍在锁定中：原样保持。不续期也不累加计数，否则攻击者可以靠持续尝试
        // 把受害账号的锁定无限延长，把一个短惩罚变成永久拒绝服务。
        if (existing.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return existing;
        }

        // 锁定刚过期，或窗口已过：重新开一个窗口从 1 计数。
        // 锁定结束后必须清零——若保留「已达阈值」的旧计数，锁定一到期就会被下一次
        // 尝试立刻重新触发，效果等同于永久锁定。
        if (existing.LockedUntil is not null || now - existing.WindowStart > _throttle.FailureWindow)
        {
            return new FailureWindow(Count: 1, WindowStart: now, LockedUntil: null);
        }

        int count = existing.Count + 1;

        return count >= threshold
            ? new FailureWindow(count, existing.WindowStart, now + _throttle.LockoutDuration)
            : new FailureWindow(count, existing.WindowStart, null);
    }

    /// <summary>机会性清理过期条目：至多每分钟一次，由首个抵达的记录者执行。</summary>
    private void CleanupIfDue(DateTimeOffset now)
    {
        long nowMs = now.ToUnixTimeMilliseconds();
        long last = Interlocked.Read(ref _lastCleanupMs);

        if (nowMs - last < CleanupIntervalMs)
        {
            return;
        }

        // 只有一个调用方能赢得这次 CAS，因此清理不会并发重入。
        if (Interlocked.CompareExchange(ref _lastCleanupMs, nowMs, last) != last)
        {
            return;
        }

        foreach ((string key, FailureWindow window) in _windows)
        {
            if (IsStale(window, now))
            {
                _windows.TryRemove(key, out _);
            }
        }
    }

    /// <summary>判断一条记录是否已不可能再影响任何判定。</summary>
    private bool IsStale(FailureWindow window, DateTimeOffset now)
        => window.LockedUntil is { } lockedUntil
            ? lockedUntil <= now
            : now - window.WindowStart > _throttle.FailureWindow;

    /// <summary>单个维度的失败窗口。不可变，靠整体替换更新。</summary>
    /// <param name="Count">窗口内的失败次数。</param>
    /// <param name="WindowStart">窗口起点。</param>
    /// <param name="LockedUntil">锁定截止时刻；未锁定为 <c>null</c>。</param>
    private sealed record FailureWindow(int Count, DateTimeOffset WindowStart, DateTimeOffset? LockedUntil);
}
