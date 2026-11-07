using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared.Audit;

namespace AnBot.Admin.Infrastructure.Audit;

/// <summary>一条待写入的审计记录。</summary>
/// <param name="Action">动作标识，如 <c>auth.login</c>。</param>
/// <param name="ActorName">操作者账号名。</param>
/// <param name="TargetType">操作对象类别。</param>
/// <param name="TargetId">操作对象标识。</param>
/// <param name="Detail">补充说明；失败时为失败原因。**不得写入凭据**。</param>
/// <param name="RemoteIp">来源 IP。</param>
/// <param name="Success">本次操作是否成功。</param>
public sealed record AuditEntry(
    string Action,
    string? ActorName = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Detail = null,
    string? RemoteIp = null,
    bool Success = true);

/// <summary>
/// 审计日志的写入与读取。
/// </summary>
/// <remarks>
/// 只提供「追加」与「读取最近若干条」两个操作：审计记录不可修改、不可删除，
/// 接口层面就不给出这种可能。
/// </remarks>
public interface IAuditLogStore
{
    /// <summary>追加一条审计记录。</summary>
    /// <param name="entry">记录内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 实现**不向调用方抛写库异常**——审计写失败只记录日志。理由见实现类型说明。
    /// </remarks>
    ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>读取最近若干条审计记录，最新的在前。</summary>
    /// <param name="maxCount">最多返回多少条；非正值取实现上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<AuditLogInfo>> GetRecentAsync(int maxCount, CancellationToken cancellationToken);
}

/// <summary>基于 EF Core 的审计日志存储。</summary>
/// <remarks>
/// <para>
/// <b>写失败为何不向上抛</b>：审计与业务是两条独立的持久化路径，若数据库暂时不可用就让
/// 登录一并失败，运维会因为「记不上账」而进不去后台，代价高于收益。失败一律以 Error 级别
/// 记录，保证可被发现——这不是静默吞异常。
/// </para>
/// <para>写入用独立的短生命周期上下文，不与调用方的业务事务共享。</para>
/// </remarks>
public sealed class AuditLogStore : IAuditLogStore
{
    /// <summary>单次查询返回条数的上限，防止后台一次拉走整张表。</summary>
    private const int MaxPageSize = 500;

    private readonly IDbContextFactory<AdminDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditLogStore> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="time">时间源。</param>
    /// <param name="logger">结构化日志。</param>
    public AuditLogStore(
        IDbContextFactory<AdminDbContext> dbFactory,
        TimeProvider time,
        ILogger<AuditLogStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await using AdminDbContext db = await _dbFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            db.AuditLogs.Add(new AuditLog
            {
                OccurredAt = _time.GetUtcNow(),
                Action = entry.Action,
                ActorName = entry.ActorName,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                Detail = entry.Detail,
                RemoteIp = entry.RemoteIp,
                Success = entry.Success,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 请求被取消（客户端断开）属于正常控制流，向上传播。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "审计记录写入失败：{Action}（操作者 {Actor}）。业务流程不受影响，但这条记录已丢失。",
                entry.Action,
                entry.ActorName ?? "未知");
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AuditLogInfo>> GetRecentAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        int limit = maxCount <= 0 ? MaxPageSize : Math.Min(maxCount, MaxPageSize);

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // 同一时刻（精度内）可能有多条，用自增主键兜底保证顺序稳定。
        List<AuditLog> logs = await db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(log => log.OccurredAt)
            .ThenByDescending(log => log.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. logs.Select(static log => new AuditLogInfo(
            log.Id,
            log.OccurredAt,
            log.Action,
            log.ActorName,
            log.TargetType,
            log.TargetId,
            log.Detail,
            log.RemoteIp,
            log.Success))];
    }
}
