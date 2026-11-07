using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;
using AnBot.Admin.Shared.Auth;

namespace AnBot.Admin.Infrastructure.Auth;

/// <summary>后台认证：登录、刷新令牌轮换、登出。</summary>
/// <remarks>
/// 全部失败路径都返回结果对象或 <c>false</c> 而不抛异常——认证失败的细节
/// （账号不存在 / 已停用 / 密码错误）不对外区分，避免成为账号枚举的信息源；
/// 真实原因进审计与结构化日志。
/// </remarks>
public interface IAuthenticationService
{
    /// <summary>用账号密码换取令牌对。</summary>
    /// <param name="userName">用户名。</param>
    /// <param name="password">密码明文。</param>
    /// <param name="remoteIp">来源 IP，用于审计与限流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功、凭据错误或被限流三种结局之一。后两种不区分具体原因。</returns>
    ValueTask<LoginAttemptResult> LoginAsync(
        string userName,
        string password,
        string? remoteIp,
        CancellationToken cancellationToken);

    /// <summary>用刷新令牌换取新的令牌对，同时轮换（作废旧的）刷新令牌。</summary>
    /// <param name="refreshToken">刷新令牌明文。</param>
    /// <param name="remoteIp">来源 IP，用于审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功返回新的令牌对；令牌无效、已撤销或已过期返回 <c>null</c>。</returns>
    ValueTask<TokenPairResponse?> RefreshAsync(
        string refreshToken,
        string? remoteIp,
        CancellationToken cancellationToken);

    /// <summary>登出：主动撤销一个刷新令牌。</summary>
    /// <param name="refreshToken">刷新令牌明文。</param>
    /// <param name="remoteIp">来源 IP，用于审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实撤销了一个有效令牌返回 <c>true</c>。</returns>
    /// <remarks>
    /// 只撤销刷新令牌。已签发的访问令牌是自包含的，在到期前仍然有效——
    /// 这是无状态令牌的固有取舍，访问令牌有效期较短（默认 30 分钟）即为对冲。
    /// </remarks>
    ValueTask<bool> LogoutAsync(string refreshToken, string? remoteIp, CancellationToken cancellationToken);
}

/// <summary>基于 EF Core 的认证实现。</summary>
public sealed class AuthenticationService : IAuthenticationService
{
    private readonly IDbContextFactory<AdminDbContext> _dbFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly TokenService _tokens;
    private readonly IAuditLogStore _audit;
    private readonly ILoginThrottleService _throttle;
    private readonly AdminAuthOptions _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthenticationService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="passwordHasher">密码哈希。</param>
    /// <param name="tokens">令牌服务。</param>
    /// <param name="audit">审计日志存储。</param>
    /// <param name="throttle">登录限流。</param>
    /// <param name="options">后台配置。</param>
    /// <param name="time">时间源。</param>
    /// <param name="logger">结构化日志。</param>
    public AuthenticationService(
        IDbContextFactory<AdminDbContext> dbFactory,
        IPasswordHasher passwordHasher,
        TokenService tokens,
        IAuditLogStore audit,
        ILoginThrottleService throttle,
        IOptions<AdminOptions> options,
        TimeProvider time,
        ILogger<AuthenticationService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _passwordHasher = passwordHasher;
        _tokens = tokens;
        _audit = audit;
        _throttle = throttle;
        _auth = options.Value.Auth;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<LoginAttemptResult> LoginAsync(
        string userName,
        string password,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

        // 限流判定排在最前，且不查库、不算哈希：锁定期间继续为每个请求付出一次
        // PBKDF2 会白白烧掉 CPU，恰好抵消掉限流本应提供的防护。
        //
        // 「是否被锁定」只看请求自带的用户名与 IP 加进程内计数，与账号是否真实存在
        // 完全无关，因此这条提前返回的路径不构成新的账号枚举信道。
        if (_throttle.TryGetLockout(userName, remoteIp, out TimeSpan retryAfter))
        {
            string wait = DescribeDuration(retryAfter);

            _logger.LogWarning(
                "后台登录被限流：账号 {UserName}，来源 {RemoteIp}，还需等待 {RetryAfter}。",
                userName,
                remoteIp ?? "未知",
                wait);

            await _audit.RecordAsync(
                new AuditEntry(
                    Action: "auth.login",
                    ActorName: userName,
                    Detail: $"登录失败次数过多，已被限流（还需等待 {wait}）",
                    RemoteIp: remoteIp,
                    Success: false),
                cancellationToken).ConfigureAwait(false);

            return LoginAttemptResult.Locked(retryAfter);
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminUser? user = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.UserName == userName, cancellationToken)
            .ConfigureAwait(false);

        // 账号不存在时也走一次等价的哈希校验（传 null 让实现做占位计算），
        // 使两条失败路径耗时一致，堵住按响应时间枚举用户名的时间侧信道。
        bool passwordMatches = _passwordHasher.Verify(password, user?.PasswordHash);

        if (user is null || !user.IsEnabled || !passwordMatches)
        {
            // 账号不存在同样计数：否则攻击者可以拿一串不存在的用户名无限试，
            // 只要避开真实用户名就永远不触发限流。
            _throttle.RecordFailure(userName, remoteIp);

            _logger.LogWarning("后台登录失败：账号 {UserName}，来源 {RemoteIp}。", userName, remoteIp ?? "未知");

            await _audit.RecordAsync(
                new AuditEntry(
                    Action: "auth.login",
                    ActorName: userName,
                    Detail: "账号不存在、已停用或密码错误",
                    RemoteIp: remoteIp,
                    Success: false),
                cancellationToken).ConfigureAwait(false);

            return LoginAttemptResult.InvalidCredentials;
        }

        _throttle.RecordSuccess(userName);

        user.LastLoginAt = _time.GetUtcNow();
        TokenPairResponse response = await IssueTokensAsync(db, user, cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            new AuditEntry(
                Action: "auth.login",
                ActorName: user.UserName,
                TargetType: "admin-user",
                TargetId: user.Id.ToString(CultureInfo.InvariantCulture),
                RemoteIp: remoteIp),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("后台登录成功：账号 {UserName}，来源 {RemoteIp}。", user.UserName, remoteIp ?? "未知");

        return LoginAttemptResult.Success(response);
    }

    /// <inheritdoc />
    public async ValueTask<TokenPairResponse?> RefreshAsync(
        string refreshToken,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        RefreshToken? stored = await FindTokenAsync(db, refreshToken, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();

        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt <= now)
        {
            _logger.LogWarning("后台令牌刷新失败：令牌无效、已撤销或已过期，来源 {RemoteIp}。", remoteIp ?? "未知");

            await _audit.RecordAsync(
                new AuditEntry(
                    Action: "auth.refresh",
                    Detail: "刷新令牌无效、已撤销或已过期",
                    RemoteIp: remoteIp,
                    Success: false),
                cancellationToken).ConfigureAwait(false);

            return null;
        }

        AdminUser? user = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == stored.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !user.IsEnabled)
        {
            await _audit.RecordAsync(
                new AuditEntry(
                    Action: "auth.refresh",
                    Detail: "令牌所属账号不存在或已停用",
                    RemoteIp: remoteIp,
                    Success: false),
                cancellationToken).ConfigureAwait(false);

            return null;
        }

        // 轮换：旧令牌立即作废，与新令牌在同一事务里落库。
        // 这样即使旧令牌此前已经泄露，它也只能用一次——第二次使用会落在「已撤销」分支。
        stored.RevokedAt = now;

        TokenPairResponse response = await IssueTokensAsync(db, user, cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            new AuditEntry(
                Action: "auth.refresh",
                ActorName: user.UserName,
                TargetType: "admin-user",
                TargetId: user.Id.ToString(CultureInfo.InvariantCulture),
                RemoteIp: remoteIp),
            cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <inheritdoc />
    public async ValueTask<bool> LogoutAsync(
        string refreshToken,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        RefreshToken? stored = await FindTokenAsync(db, refreshToken, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.RevokedAt is not null)
        {
            return false;
        }

        stored.RevokedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        AdminUser? user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == stored.UserId, cancellationToken)
            .ConfigureAwait(false);

        await _audit.RecordAsync(
            new AuditEntry(
                Action: "auth.logout",
                ActorName: user?.UserName,
                TargetType: "admin-user",
                TargetId: stored.UserId.ToString(CultureInfo.InvariantCulture),
                RemoteIp: remoteIp),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>把等待时长写成便于阅读的中文描述，向上取整到分钟/秒。</summary>
    private static string DescribeDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? $"{Math.Ceiling(duration.TotalMinutes)} 分钟"
            : $"{Math.Ceiling(duration.TotalSeconds)} 秒";

    /// <summary>按明文哈希查找刷新令牌。</summary>
    private static Task<RefreshToken?> FindTokenAsync(
        AdminDbContext db,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        string hash = TokenService.HashRefreshToken(refreshToken);
        return db.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
    }

    /// <summary>签发令牌对并把新的刷新令牌写入上下文（含调用方已做的其它改动，一次保存）。</summary>
    private async ValueTask<TokenPairResponse> IssueTokensAsync(
        AdminDbContext db,
        AdminUser user,
        CancellationToken cancellationToken)
    {
        (string accessToken, DateTimeOffset accessExpiresAt) = _tokens.CreateAccessToken(user);
        (string refreshPlain, string refreshHash) = TokenService.CreateRefreshToken();

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset refreshExpiresAt = now + _auth.RefreshTokenLifetime;

        db.RefreshTokens.Add(new RefreshToken
        {
            TokenHash = refreshHash,
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = refreshExpiresAt,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new TokenPairResponse(
            accessToken,
            accessExpiresAt,
            refreshPlain,
            refreshExpiresAt,
            user.UserName);
    }
}
