using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Infrastructure;
using PulseBot.Admin.Infrastructure.Audit;
using PulseBot.Admin.Infrastructure.Auth;
using PulseBot.Admin.Infrastructure.Entities;
using PulseBot.Admin.Shared;
using PulseBot.Admin.Shared.Auth;

namespace PulseBot.Admin.Tests;

/// <summary>
/// 认证服务测试：登录的成功与各类失败路径、刷新令牌的轮换与撤销、过期判定，以及审计落库。
/// </summary>
/// <remarks>
/// 依赖全部是真的：真实的 PBKDF2 哈希、真实的内存 SQLite、真实的审计写入。
/// 认证是安全边界，用假替身测出来的「通过」说明不了问题。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuthenticationServiceTests
{
    private const string SigningKey = "unit-test-signing-key-0123456789abcdef";
    private const string RemoteIp = "127.0.0.1";

    /// <summary>用例里的账号阈值。取小值，让「达到阈值」只需几次调用。</summary>
    private const int FailuresBeforeLockout = 3;

    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>密码正确时登录成功，签发完整令牌对，并更新最后登录时刻。</summary>
    [Fact]
    public async Task 正确密码登录成功并签发令牌对()
    {
        await using var harness = await Harness.CreateAsync();

        TokenPairResponse tokens = await harness.LoginSuccessAsync();

        Assert.Equal("admin", tokens.UserName);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.True(tokens.AccessTokenExpiresAt > harness.Clock.GetUtcNow());
        Assert.True(tokens.RefreshTokenExpiresAt > tokens.AccessTokenExpiresAt);

        AdminUser user = await harness.GetUserAsync();
        Assert.True(user.LastLoginAt.HasValue);
        Assert.Equal(harness.Clock.GetUtcNow(), user.LastLoginAt.Value);
    }

    /// <summary>密码错误时登录失败，且不更新最后登录时刻。</summary>
    [Fact]
    public async Task 密码错误登录失败()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Equal(LoginOutcome.InvalidCredentials, (await harness.LoginAsync("wrong-password")).Outcome);

        Assert.Null((await harness.GetUserAsync()).LastLoginAt);
    }

    /// <summary>账号不存在时同样返回失败，对外不区分「账号不存在」与「密码错误」。</summary>
    [Fact]
    public async Task 账号不存在登录失败()
    {
        await using var harness = await Harness.CreateAsync();

        LoginAttemptResult result = await harness.Auth
            .LoginAsync("nobody", "any-password", RemoteIp, CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.Tokens);
    }

    /// <summary>账号被停用后无法登录。</summary>
    [Fact]
    public async Task 停用的账号无法登录()
    {
        await using var harness = await Harness.CreateAsync(userEnabled: false);

        Assert.Equal(LoginOutcome.InvalidCredentials, (await harness.LoginAsync()).Outcome);
    }

    /// <summary>刷新会轮换刷新令牌：旧令牌立即失效，新令牌可以继续使用。</summary>
    [Fact]
    public async Task 刷新令牌轮换后旧令牌失效()
    {
        await using var harness = await Harness.CreateAsync();
        TokenPairResponse first = await harness.LoginSuccessAsync();

        TokenPairResponse second = Assert.IsType<TokenPairResponse>(
            await harness.Auth.RefreshAsync(first.RefreshToken, RemoteIp, CancellationToken.None));

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // 旧令牌重放被拒绝——即使它此前泄露过，也只能用一次。
        Assert.Null(await harness.Auth.RefreshAsync(first.RefreshToken, RemoteIp, CancellationToken.None));

        // 新令牌仍然可用。
        Assert.NotNull(await harness.Auth.RefreshAsync(second.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>登出后该刷新令牌不再可用。</summary>
    [Fact]
    public async Task 登出后刷新令牌失效()
    {
        await using var harness = await Harness.CreateAsync();
        TokenPairResponse tokens = await harness.LoginSuccessAsync();

        Assert.True(await harness.Auth.LogoutAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None));

        Assert.Null(await harness.Auth.RefreshAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>重复登出返回 <c>false</c> 而不是抛异常——登出必须幂等。</summary>
    [Fact]
    public async Task 重复登出返回false()
    {
        await using var harness = await Harness.CreateAsync();
        TokenPairResponse tokens = await harness.LoginSuccessAsync();

        await harness.Auth.LogoutAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None);

        Assert.False(await harness.Auth.LogoutAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>超过有效期的刷新令牌不可用。</summary>
    [Fact]
    public async Task 过期的刷新令牌不可用()
    {
        await using var harness = await Harness.CreateAsync(refreshLifetime: TimeSpan.FromDays(7));
        TokenPairResponse tokens = await harness.LoginSuccessAsync();

        harness.Clock.Advance(TimeSpan.FromDays(8));

        Assert.Null(await harness.Auth.RefreshAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>空白刷新令牌直接判失败，不查库。</summary>
    [Fact]
    public async Task 空白刷新令牌判失败()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Null(await harness.Auth.RefreshAsync("  ", RemoteIp, CancellationToken.None));
        Assert.False(await harness.Auth.LogoutAsync("  ", RemoteIp, CancellationToken.None));
    }

    /// <summary>登录与刷新都会写入审计记录，成功与失败分别标注。</summary>
    [Fact]
    public async Task 登录与刷新写入审计记录()
    {
        await using var harness = await Harness.CreateAsync();

        TokenPairResponse tokens = await harness.LoginSuccessAsync();
        await harness.Auth.RefreshAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None);
        await harness.LoginAsync("wrong-password");

        IReadOnlyList<AuditLog> logs = await harness.ListAuditAsync();

        Assert.Equal(
            ["auth.login", "auth.refresh", "auth.login"],
            logs.Select(log => log.Action));
        Assert.Equal([true, true, false], logs.Select(log => log.Success));
        Assert.Equal("admin", logs[0].ActorName);
        Assert.Equal(RemoteIp, logs[0].RemoteIp);
    }

    /// <summary>
    /// 失败次数达到阈值后，即使密码正确也一律拒绝。
    /// </summary>
    /// <remarks>
    /// 限流判定发生在查库与哈希之前，因此锁定期内不会因为「这次密码恰好是对的」而放行——
    /// 否则攻击者只需在锁定期间持续尝试，撞对一次就绕过了限流。
    /// </remarks>
    [Fact]
    public async Task 达到阈值后正确密码也被拒绝()
    {
        await using var harness = await Harness.CreateAsync(throttle: NewThrottle());

        for (int i = 0; i < FailuresBeforeLockout; i++)
        {
            await harness.LoginAsync("wrong-password");
        }

        LoginAttemptResult result = await harness.LoginAsync();

        Assert.Equal(LoginOutcome.Locked, result.Outcome);
        Assert.Null(result.Tokens);
        Assert.True(result.RetryAfter > TimeSpan.Zero);
    }

    /// <summary>对不存在的账号同样计数——否则攻击者只要避开真实用户名就能永不触发限流。</summary>
    [Fact]
    public async Task 账号不存在也计入限流()
    {
        await using var harness = await Harness.CreateAsync(throttle: NewThrottle());

        for (int i = 0; i < FailuresBeforeLockout; i++)
        {
            await harness.Auth.LoginAsync("nobody", "any-password", RemoteIp, CancellationToken.None);
        }

        LoginAttemptResult result = await harness.Auth
            .LoginAsync("nobody", "any-password", RemoteIp, CancellationToken.None);

        Assert.Equal(LoginOutcome.Locked, result.Outcome);
    }

    /// <summary>成功登录后账号的失败计数清零，重新从零开始累计。</summary>
    [Fact]
    public async Task 成功登录重置账号失败计数()
    {
        await using var harness = await Harness.CreateAsync(throttle: NewThrottle());

        await harness.LoginAsync("wrong-password");
        await harness.LoginAsync("wrong-password");

        await harness.LoginSuccessAsync();

        await harness.LoginAsync("wrong-password");
        await harness.LoginAsync("wrong-password");

        // 若成功登录没有清零，此处累计已达阈值（5 > 3），结局会是 Locked。
        Assert.Equal(LoginOutcome.InvalidCredentials, (await harness.LoginAsync("wrong-password")).Outcome);
    }

    /// <summary>被限流的请求同样落审计，且与普通失败在文字上可区分。</summary>
    [Fact]
    public async Task 限流拒绝写入审计()
    {
        await using var harness = await Harness.CreateAsync(throttle: NewThrottle());

        for (int i = 0; i < FailuresBeforeLockout; i++)
        {
            await harness.LoginAsync("wrong-password");
        }

        await harness.LoginAsync("wrong-password");

        IReadOnlyList<AuditLog> logs = await harness.ListAuditAsync();

        Assert.Equal(FailuresBeforeLockout + 1, logs.Count);
        Assert.All(logs, log => Assert.False(log.Success));
        Assert.Contains("限流", logs[^1].Detail, StringComparison.Ordinal);
    }

    // ---------- 测试基础设施 ----------

    /// <summary>
    /// 用例用的限流配置：账号阈值取小值；IP 阈值刻意放大，
    /// 使每个用例只考察账号维度，不被同一来源的累计失败抢先触发。
    /// </summary>
    private static AdminThrottleOptions NewThrottle() => new()
    {
        FailureWindow = TimeSpan.FromMinutes(15),
        MaxFailuresPerUser = FailuresBeforeLockout,
        MaxFailuresPerIp = 100,
        LockoutDuration = TimeSpan.FromMinutes(10),
    };

    /// <summary>一次装配好的认证服务：真实哈希、真实令牌、真实审计，共用同一个内存库与时钟。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private const string Password = "correct-password";

        private Harness(AdminTestDatabase database, MutableTimeProvider clock, AuthenticationService auth)
        {
            Database = database;
            Clock = clock;
            Auth = auth;
        }

        public AdminTestDatabase Database { get; }

        public MutableTimeProvider Clock { get; }

        public AuthenticationService Auth { get; }

        /// <summary>建库、装配服务，并写入一个可登录的账号。</summary>
        /// <param name="userEnabled">账号是否启用。</param>
        /// <param name="refreshLifetime">刷新令牌有效期。</param>
        /// <param name="throttle">限流配置；为 <c>null</c> 时用默认值。</param>
        public static async Task<Harness> CreateAsync(
            bool userEnabled = true,
            TimeSpan? refreshLifetime = null,
            AdminThrottleOptions? throttle = null)
        {
            AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
            var clock = new MutableTimeProvider(Start);
            var hasher = new Pbkdf2PasswordHasher();

            var options = Options.Create(new AdminOptions
            {
                Auth = new AdminAuthOptions
                {
                    SigningKey = SigningKey,
                    Issuer = "pulsebot-test",
                    Audience = "pulsebot-test",
                    RefreshTokenLifetime = refreshLifetime ?? TimeSpan.FromDays(7),
                },
                Throttle = throttle ?? new AdminThrottleOptions(),
            });

            var audit = new AuditLogStore(database.Factory, clock, NullLogger<AuditLogStore>.Instance);
            var tokens = new TokenService(options, clock);
            var auth = new AuthenticationService(
                database.Factory,
                hasher,
                tokens,
                audit,
                new InMemoryLoginThrottleService(options, clock),
                options,
                clock,
                NullLogger<AuthenticationService>.Instance);

            var harness = new Harness(database, clock, auth);

            await using (AdminDbContext db = database.Factory.CreateDbContext())
            {
                db.Users.Add(new AdminUser
                {
                    UserName = "admin",
                    PasswordHash = hasher.Hash(Password),
                    Role = "admin",
                    IsEnabled = userEnabled,
                    CreatedAt = clock.GetUtcNow(),
                });

                await db.SaveChangesAsync();
            }

            return harness;
        }

        /// <summary>以内置账号执行一次登录，返回完整结局。</summary>
        public ValueTask<LoginAttemptResult> LoginAsync(string password = Password)
            => Auth.LoginAsync("admin", password, RemoteIp, CancellationToken.None);

        /// <summary>以内置账号执行一次必然成功的登录，返回令牌对。</summary>
        public async Task<TokenPairResponse> LoginSuccessAsync(string password = Password)
        {
            LoginAttemptResult result = await LoginAsync(password);

            Assert.Equal(LoginOutcome.Success, result.Outcome);

            return Assert.IsType<TokenPairResponse>(result.Tokens);
        }

        /// <summary>读取内置账号的当前状态。</summary>
        public async Task<AdminUser> GetUserAsync()
        {
            await using AdminDbContext db = Database.Factory.CreateDbContext();
            return await db.Users.AsNoTracking().SingleAsync(user => user.UserName == "admin");
        }

        /// <summary>读取全部审计记录，按写入先后排列。</summary>
        public async Task<IReadOnlyList<AuditLog>> ListAuditAsync()
        {
            await using AdminDbContext db = Database.Factory.CreateDbContext();
            return await db.AuditLogs.AsNoTracking().OrderBy(log => log.Id).ToListAsync();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
