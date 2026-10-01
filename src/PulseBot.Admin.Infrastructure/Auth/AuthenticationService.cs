using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Infrastructure.Audit;
using PulseBot.Admin.Infrastructure.Entities;
using PulseBot.Admin.Shared;
using PulseBot.Admin.Shared.Auth;

namespace PulseBot.Admin.Infrastructure.Auth;

/// <summary>后台认证：登录、刷新令牌轮换、登出。</summary>
/// <remarks>
/// 全部失败路径都返回 <c>null</c>/<c>false</c> 而不抛异常——认证失败的细节
/// （账号不存在 / 已停用 / 密码错误）不对外区分，避免成为账号枚举的信息源；
/// 真实原因进审计与结构化日志。
/// </remarks>
public interface IAuthenticationService
{
    /// <summary>用账号密码换取令牌对。</summary>
    /// <param name="userName">用户名。</param>
    /// <param name="password">密码明文。</param>
    /// <param name="remoteIp">来源 IP，用于审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功返回令牌对；账号不存在、已停用或密码错误返回 <c>null</c>。</returns>
    ValueTask<TokenPairResponse?> LoginAsync(
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
    private readonly AdminAuthOptions _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthenticationService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="passwordHasher">密码哈希。</param>
    /// <param name="tokens">令牌服务。</param>
    /// <param name="audit">审计日志存储。</param>
    /// <param name="options">后台配置。</param>
    /// <param name="time">时间源。</param>
    /// <param name="logger">结构化日志。</param>
    public AuthenticationService(
        IDbContextFactory<AdminDbContext> dbFactory,
        IPasswordHasher passwordHasher,
        TokenService tokens,
        IAuditLogStore audit,
        IOptions<AdminOptions> options,
        TimeProvider time,
        ILogger<AuthenticationService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _passwordHasher = passwordHasher;
        _tokens = tokens;
        _audit = audit;
        _auth = options.Value.Auth;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<TokenPairResponse?> LoginAsync(
        string userName,
        string password,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

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
            _logger.LogWarning("后台登录失败：账号 {UserName}，来源 {RemoteIp}。", userName, remoteIp ?? "未知");

            await _audit.RecordAsync(
                new AuditEntry(
                    Action: "auth.login",
                    ActorName: userName,
                    Detail: "账号不存在、已停用或密码错误",
                    RemoteIp: remoteIp,
                    Success: false),
                cancellationToken).ConfigureAwait(false);

            return null;
        }

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

        return response;
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
