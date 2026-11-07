using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Tests;

/// <summary>
/// 登录限流的计数、锁定与清除语义。
/// </summary>
/// <remarks>
/// 时间源可控，因此「窗口过期」「锁定到期」这类需要等待的分支可以精确断言，
/// 而不是靠 <c>Task.Delay</c> 拼运气。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class LoginThrottleServiceTests
{
    private const string User = "admin";
    private const string Ip = "203.0.113.7";
    private const int UserLimit = 3;
    private const int IpLimit = 5;

    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(10);

    /// <summary>没有任何失败记录时不是锁定状态。</summary>
    [Fact]
    public void 初始状态未锁定()
    {
        InMemoryLoginThrottleService service = NewService(new MutableTimeProvider(Start));

        Assert.False(service.TryGetLockout(User, Ip, out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    /// <summary>失败次数未达阈值时不锁定。</summary>
    [Fact]
    public void 未达阈值时不锁定()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        service.RecordFailure(User, Ip);
        service.RecordFailure(User, Ip);

        Assert.False(service.TryGetLockout(User, Ip, out _));
    }

    /// <summary>失败次数达到账号阈值即锁定，剩余等待时间等于完整锁定时长。</summary>
    [Fact]
    public void 达到账号阈值即锁定()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 0; i < UserLimit; i++)
        {
            service.RecordFailure(User, Ip);
        }

        Assert.True(service.TryGetLockout(User, Ip, out TimeSpan retryAfter));
        Assert.Equal(Lockout, retryAfter);
    }

    /// <summary>
    /// 锁定期间继续失败不会延长锁定。
    /// </summary>
    /// <remarks>
    /// 若续期，攻击者只要持续发请求就能把受害账号的锁定无限延长，
    /// 把一个十分钟的惩罚变成永久拒绝服务。
    /// </remarks>
    [Fact]
    public void 锁定期间继续失败不延长锁定()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 0; i < UserLimit; i++)
        {
            service.RecordFailure(User, Ip);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        service.RecordFailure(User, Ip);

        clock.Advance(TimeSpan.FromMinutes(8));

        // 仍在原始锁定期的最后一分钟里：剩余 1 分钟，而不是「从这次失败起再锁 10 分钟」。
        Assert.True(service.TryGetLockout(User, Ip, out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
    }

    /// <summary>
    /// 锁定到期后计数清零，不会「一到期就被下一次失败重新锁上」。
    /// </summary>
    /// <remarks>
    /// 若保留达阈值的旧计数，锁定期一过第一次尝试就会再次触发锁定，
    /// 闭式效果同样等于永久锁定。
    /// </remarks>
    [Fact]
    public void 锁定到期后重新计数()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 0; i < UserLimit; i++)
        {
            service.RecordFailure(User, Ip);
        }

        clock.Advance(Lockout + TimeSpan.FromSeconds(1));

        Assert.False(service.TryGetLockout(User, Ip, out _));

        service.RecordFailure(User, Ip);

        Assert.False(service.TryGetLockout(User, Ip, out _));
    }

    /// <summary>超出时间窗口的失败不再累计，窗口从头开始。</summary>
    [Fact]
    public void 窗口过期后计数清零()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        service.RecordFailure(User, Ip);
        service.RecordFailure(User, Ip);

        clock.Advance(Window + TimeSpan.FromSeconds(1));

        service.RecordFailure(User, Ip);

        // 若旧计数仍在，此处累计已达阈值（3）。
        Assert.False(service.TryGetLockout(User, Ip, out _));
    }

    /// <summary>IP 维度独立计数：单账号都没达阈值，但同一来源的失败累计会触发 IP 锁定。</summary>
    [Fact]
    public void 同一来源的多次失败触发IP锁定()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 1; i <= IpLimit; i++)
        {
            service.RecordFailure($"user-{i}", Ip);
        }

        // 每个账号各失败一次，账号维度远未达阈值……
        Assert.False(service.TryGetLockout("user-1", "198.51.100.9", out _));

        // ……但原来源已被锁。
        Assert.True(service.TryGetLockout("user-1", Ip, out _));
    }

    /// <summary>两个维度同时锁定时，返回两者中更长的等待时间。</summary>
    /// <remarks>
    /// 只报较短的会让客户端按它重试后立刻又被另一个维度拒掉，看起来像限流没有生效。
    /// </remarks>
    [Fact]
    public void 两维度都锁定时取较长等待()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 0; i < UserLimit; i++)
        {
            service.RecordFailure(User, Ip);
        }

        // 账号在第 3 次失败时锁定（到 T+10）；IP 要到第 5 次才锁定，因此截止更晚（T+12）。
        clock.Advance(TimeSpan.FromMinutes(2));
        service.RecordFailure(User, Ip);
        service.RecordFailure(User, Ip);

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(service.TryGetLockout(User, Ip, out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(9), retryAfter);
    }

    /// <summary>成功登录清除该账号的失败计数——用户自己输对了，此前多半是手误。</summary>
    [Fact]
    public void 成功登录清除该账号计数()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        service.RecordFailure(User, Ip);
        service.RecordFailure(User, Ip);
        service.RecordSuccess(User);
        service.RecordFailure(User, Ip);
        service.RecordFailure(User, Ip);

        // 若未清除，累计已达 4 次（阈值 3）。
        Assert.False(service.TryGetLockout(User, Ip, out _));
    }

    /// <summary>
    /// 成功登录不清除 IP 维度的计数。
    /// </summary>
    /// <remarks>
    /// 否则「用一个自己的账号反复成功登录」就成了重置 IP 计数的绕过手法，
    /// 而那正是 IP 维度要拦的场景（同一来源对多个账号的尝试）。
    /// </remarks>
    [Fact]
    public void 成功登录不清除IP计数()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        service.RecordFailure("user-a", Ip);
        service.RecordFailure("user-a", Ip);
        service.RecordFailure("user-b", Ip);
        service.RecordFailure("user-b", Ip);

        service.RecordSuccess("user-a");
        service.RecordFailure("user-c", Ip);

        Assert.True(service.TryGetLockout("user-c", Ip, out _));
    }

    /// <summary>取不到来源 IP 时只按账号维度计数。</summary>
    [Fact]
    public void 无来源IP时只按账号计数()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 0; i < UserLimit; i++)
        {
            service.RecordFailure(User, remoteIp: null);
        }

        Assert.True(service.TryGetLockout(User, remoteIp: null, out _));

        // 账号桶与来源无关，换个 IP 一样被拒；但别的账号不受影响。
        Assert.True(service.TryGetLockout(User, Ip, out _));
        Assert.False(service.TryGetLockout("other", Ip, out _));
    }

    /// <summary>机会性清理不会误删仍然有效的锁定。</summary>
    [Fact]
    public void 清理不会移除仍有效的锁定()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(clock);

        for (int i = 0; i < UserLimit; i++)
        {
            service.RecordFailure(User, Ip);
        }

        // 跨过清理间隔（1 分钟）后再记一次失败，触发清理。
        clock.Advance(TimeSpan.FromMinutes(2));
        service.RecordFailure("someone-else", "198.51.100.9");

        Assert.True(service.TryGetLockout(User, Ip, out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(8), retryAfter);
    }

    /// <summary>阈值为 1 时首次失败即锁定——新建条目与更新走的是同一条推进逻辑。</summary>
    [Fact]
    public void 阈值为一时首次失败即锁定()
    {
        var clock = new MutableTimeProvider(Start);
        InMemoryLoginThrottleService service = NewService(
            clock,
            new AdminThrottleOptions
            {
                FailureWindow = Window,
                MaxFailuresPerUser = 1,
                MaxFailuresPerIp = IpLimit,
                LockoutDuration = Lockout,
            });

        service.RecordFailure(User, Ip);

        Assert.True(service.TryGetLockout(User, Ip, out _));
    }

    // ---------- 测试基础设施 ----------

    private static InMemoryLoginThrottleService NewService(
        MutableTimeProvider clock,
        AdminThrottleOptions? throttle = null) =>
        new(
            Options.Create(new AdminOptions
            {
                Throttle = throttle ?? new AdminThrottleOptions
                {
                    FailureWindow = Window,
                    MaxFailuresPerUser = UserLimit,
                    MaxFailuresPerIp = IpLimit,
                    LockoutDuration = Lockout,
                },
            }),
            clock);
}
