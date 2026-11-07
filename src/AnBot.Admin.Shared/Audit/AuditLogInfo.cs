namespace AnBot.Admin.Shared.Audit;

/// <summary>
/// 一条审计记录。
/// </summary>
/// <remarks>
/// 审计记录只增不改：一旦落库，没有任何接口能修改或删除它。
/// <see cref="Detail"/> 里不得写入密码、令牌等凭据——审计日志是长期保留且可被后台直接展示的。
/// </remarks>
/// <param name="Id">自增主键。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
/// <param name="Action">动作标识，如 <c>auth.login</c>、<c>auth.refresh</c>、<c>auth.logout</c>。</param>
/// <param name="ActorName">操作者账号名；登录失败时为尝试使用的用户名。</param>
/// <param name="TargetType">操作对象的类别；无对象时为 <c>null</c>。</param>
/// <param name="TargetId">操作对象的标识；无对象时为 <c>null</c>。</param>
/// <param name="Detail">补充说明；失败时为失败原因。</param>
/// <param name="RemoteIp">来源 IP。</param>
/// <param name="Success">本次操作是否成功。</param>
public sealed record AuditLogInfo(
    long Id,
    DateTimeOffset OccurredAt,
    string Action,
    string? ActorName,
    string? TargetType,
    string? TargetId,
    string? Detail,
    string? RemoteIp,
    bool Success);
