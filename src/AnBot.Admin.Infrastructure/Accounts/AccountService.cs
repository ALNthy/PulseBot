using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;
using AnBot.Admin.Shared.Accounts;

namespace AnBot.Admin.Infrastructure.Accounts;

/// <summary>
/// 账号管理：列举、创建、改密、重置密码、启停、强制下线。
/// </summary>
/// <remarks>
/// <para>
/// 写操作一律落审计（动作前缀 <c>account.</c>）。审计写在服务层而非接口层，
/// 是为了让「每个改动账号的路径都留下记录」成为本类型的职责，
/// 不会因为某个端点忘记而漏掉。
/// </para>
/// <para>
/// 失败路径不抛异常：这些都是预期内的业务拒绝，调用方需要据此返回
/// 404/401/409/400，用异常表达会迫使接口层靠捕获类型来分辨。
/// </para>
/// </remarks>
public interface IAccountService
{
    /// <summary>列出全部账号。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<AccountInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>创建账号。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="userName">登录名。</param>
    /// <param name="password">初始密码明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AccountOperationResult> CreateAsync(
        AccountActor actor,
        string userName,
        string password,
        CancellationToken cancellationToken);

    /// <summary>修改操作者自己的密码，需验证当前密码。</summary>
    /// <param name="actor">操作者（即目标账号）。</param>
    /// <param name="currentPassword">当前密码。</param>
    /// <param name="newPassword">新密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AccountOperationResult> ChangeOwnPasswordAsync(
        AccountActor actor,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);

    /// <summary>重置指定账号的密码。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="targetUserId">目标账号主键。</param>
    /// <param name="newPassword">新密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AccountOperationResult> ResetPasswordAsync(
        AccountActor actor,
        int targetUserId,
        string newPassword,
        CancellationToken cancellationToken);

    /// <summary>启用或停用账号。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="targetUserId">目标账号主键。</param>
    /// <param name="enabled">目标状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AccountOperationResult> SetEnabledAsync(
        AccountActor actor,
        int targetUserId,
        bool enabled,
        CancellationToken cancellationToken);

    /// <summary>撤销该账号的全部刷新令牌，令其已登录的会话无法续期。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="targetUserId">目标账号主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<AccountOperationResult> RevokeSessionsAsync(
        AccountActor actor,
        int targetUserId,
        CancellationToken cancellationToken);
}

/// <summary>基于 EF Core 的账号管理实现。</summary>
public sealed class AccountService : IAccountService
{
    private const string TargetType = "admin-user";

    private readonly IDbContextFactory<AdminDbContext> _dbFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogStore _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="passwordHasher">密码哈希。</param>
    /// <param name="audit">审计日志存储。</param>
    /// <param name="time">时间源。</param>
    /// <param name="logger">结构化日志。</param>
    public AccountService(
        IDbContextFactory<AdminDbContext> dbFactory,
        IPasswordHasher passwordHasher,
        IAuditLogStore audit,
        TimeProvider time,
        ILogger<AccountService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AccountInfo>> ListAsync(CancellationToken cancellationToken)
    {
        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AdminUser> users = await db.Users
            .AsNoTracking()
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. users.Select(ToInfo)];
    }

    /// <inheritdoc />
    public async ValueTask<AccountOperationResult> CreateAsync(
        AccountActor actor,
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

        string name = userName.Trim();

        if (password.Length < AccountRules.MinimumPasswordLength)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.WeakPassword);
        }

        if (name.Length > AccountRules.MaxUserNameLength)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.UserNameTaken);
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // 先查一次给出友好的 409。并发下的最后防线是数据库上的唯一索引，
        // 真撞上时 SaveChanges 会抛异常——那是 500，而不是被伪装成 409 的其它故障。
        if (await db.Users.AnyAsync(user => user.UserName == name, cancellationToken).ConfigureAwait(false))
        {
            return AccountOperationResult.Fail(AccountOperationStatus.UserNameTaken);
        }

        AdminUser created = new()
        {
            UserName = name,
            PasswordHash = _passwordHasher.Hash(password),
            Role = AdminRoles.Admin,
            IsEnabled = true,
            CreatedAt = _time.GetUtcNow(),
        };

        db.Users.Add(created);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(actor, "account.create", created.Id, $"已创建账号 {created.UserName}", cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("账号 {UserName} 已由 {ActorName} 创建。", created.UserName, actor.UserName);

        return AccountOperationResult.Ok(ToInfo(created));
    }

    /// <inheritdoc />
    public async ValueTask<AccountOperationResult> ChangeOwnPasswordAsync(
        AccountActor actor,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminUser? user = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == actor.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.NotFound);
        }

        // 先验旧密码再验新密码强度：把「新密码太短」透露给一个连旧密码都不知道的人，
        // 等于给了攻击者一个免费的校验反馈通道。
        if (!_passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            _logger.LogWarning("账号 {UserName} 修改密码失败：当前密码不正确。", user.UserName);

            return AccountOperationResult.Fail(AccountOperationStatus.InvalidCredentials);
        }

        if (newPassword.Length < AccountRules.MinimumPasswordLength)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.WeakPassword);
        }

        user.PasswordHash = _passwordHasher.Hash(newPassword);

        int revoked = await RevokeTokensAsync(db, user.Id, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            actor,
            "account.password.change",
            user.Id,
            $"已修改自己的密码，并撤销 {revoked} 个刷新令牌",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "账号 {UserName} 已修改密码，撤销了 {RevokedCount} 个刷新令牌。",
            user.UserName,
            revoked);

        return AccountOperationResult.Ok(ToInfo(user));
    }

    /// <inheritdoc />
    public async ValueTask<AccountOperationResult> ResetPasswordAsync(
        AccountActor actor,
        int targetUserId,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(newPassword);

        // 重置自己必须走「验证当前密码」的路径：否则这就是一个不需要旧密码的改密后门，
        // 只要拿到一个已登录的会话就能永久接管该账号。
        if (targetUserId == actor.UserId)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.SelfOperation);
        }

        if (newPassword.Length < AccountRules.MinimumPasswordLength)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.WeakPassword);
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminUser? target = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.NotFound);
        }

        target.PasswordHash = _passwordHasher.Hash(newPassword);

        int revoked = await RevokeTokensAsync(db, target.Id, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            actor,
            "account.password.reset",
            target.Id,
            $"已重置账号 {target.UserName} 的密码，并撤销 {revoked} 个刷新令牌",
            cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "账号 {TargetUserName} 的密码已被 {ActorName} 重置。",
            target.UserName,
            actor.UserName);

        return AccountOperationResult.Ok(ToInfo(target));
    }

    /// <inheritdoc />
    public async ValueTask<AccountOperationResult> SetEnabledAsync(
        AccountActor actor,
        int targetUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!enabled && targetUserId == actor.UserId)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.SelfOperation);
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminUser? target = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.NotFound);
        }

        if (target.IsEnabled == enabled)
        {
            // 状态本就如此：幂等成功，不写审计也不记日志，避免重复点击刷屏。
            return AccountOperationResult.Ok(ToInfo(target));
        }

        if (enabled)
        {
            await db.Users
                .Where(candidate => candidate.Id == targetUserId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.IsEnabled, true),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // 「还剩至少一个启用账号」的判定写进 UPDATE 的 WHERE，由数据库在执行这条语句时
            // 一并完成。SQLite 对 UPDATE 持写锁直到提交，因此两个并发的停用请求不可能都通过
            // 这个检查——换成「先查再写」就会留下一个能让两者同时通过的事务间隙。
            int affected = await db.Users
                .Where(candidate => candidate.Id == targetUserId
                    && db.Users.Count(other => other.IsEnabled) > 1)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.IsEnabled, false),
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
            {
                _logger.LogWarning(
                    "{ActorName} 尝试停用账号 {TargetUserName}，但它是最后一个启用的账号，已拒绝。",
                    actor.UserName,
                    target.UserName);

                return AccountOperationResult.Fail(AccountOperationStatus.LastEnabledAccount);
            }
        }

        await AuditAsync(
            actor,
            "account.set-state",
            target.Id,
            enabled ? $"已启用账号 {target.UserName}" : $"已停用账号 {target.UserName}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "账号 {TargetUserName} 已被 {ActorName} {Action}。",
            target.UserName,
            actor.UserName,
            enabled ? "启用" : "停用");

        return AccountOperationResult.Ok(ToInfo(target) with { IsEnabled = enabled });
    }

    /// <inheritdoc />
    public async ValueTask<AccountOperationResult> RevokeSessionsAsync(
        AccountActor actor,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminUser? target = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            return AccountOperationResult.Fail(AccountOperationStatus.NotFound);
        }

        int revoked = await RevokeTokensAsync(db, target.Id, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            actor,
            "account.revoke-sessions",
            target.Id,
            $"已强制下线账号 {target.UserName}，撤销 {revoked} 个刷新令牌",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "账号 {TargetUserName} 的 {RevokedCount} 个刷新令牌已被 {ActorName} 撤销。",
            target.UserName,
            revoked,
            actor.UserName);

        return AccountOperationResult.Ok(ToInfo(target));
    }

    /// <summary>
    /// 把该账号所有尚未撤销的刷新令牌标记为已撤销，返回受影响的行数。
    /// </summary>
    /// <remarks>
    /// 用 EF 的变更跟踪而不是 <c>ExecuteUpdate</c>：这样它与调用方的其它改动（改密码）
    /// 落在同一次 <c>SaveChanges</c> 里，要么都生效要么都不生效——
    /// 不会出现「密码没改成功，会话却已被踢光」。
    /// </remarks>
    private async ValueTask<int> RevokeTokensAsync(
        AdminDbContext db,
        int userId,
        CancellationToken cancellationToken)
    {
        List<RefreshToken> tokens = await db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = _time.GetUtcNow();
        foreach (RefreshToken token in tokens)
        {
            token.RevokedAt = now;
        }

        return tokens.Count;
    }

    /// <summary>记录一次成功的账号写操作。</summary>
    private ValueTask AuditAsync(
        AccountActor actor,
        string action,
        int targetId,
        string detail,
        CancellationToken cancellationToken)
        => _audit.RecordAsync(
            new AuditEntry(
                Action: action,
                ActorName: actor.UserName,
                TargetType: TargetType,
                TargetId: targetId.ToString(CultureInfo.InvariantCulture),
                Detail: detail,
                RemoteIp: actor.RemoteIp),
            cancellationToken);

    /// <summary>把实体映射为对外信息。</summary>
    private static AccountInfo ToInfo(AdminUser user)
        => new(user.Id, user.UserName, user.Role, user.IsEnabled, user.CreatedAt, user.LastLoginAt);
}
