namespace PulseBot.Admin.Infrastructure.Entities;

/// <summary>
/// 后台管理员账号。
/// </summary>
/// <remarks>
/// 这是 EF Core 的实体类型，不是对外契约——对外的账号信息请用
/// <see cref="Shared.Auth.TokenPairResponse"/> 等 DTO 承载。
/// </remarks>
public sealed class AdminUser
{
    /// <summary>自增主键。</summary>
    public int Id { get; set; }

    /// <summary>登录名（唯一）。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>密码的派生哈希（含算法、迭代次数与盐的编码串），绝不保存明文。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>角色名。第一期只有 <c>admin</c> 一种角色。</summary>
    public string Role { get; set; } = "admin";

    /// <summary>账号是否可用。停用后已签发的访问令牌仍有效，但无法再登录或刷新。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>创建时刻（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最后一次登录成功时刻（UTC）；从未登录过为 <c>null</c>。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>
/// 刷新令牌。
/// </summary>
/// <remarks>
/// <para>
/// 库里只存令牌的 SHA-256 哈希，明文仅出现在签发响应里：数据库文件一旦泄露，
/// 拿到的哈希无法直接当凭据使用。
/// </para>
/// <para>
/// 令牌是 256 位随机值而非 JWT——它不需要携带声明（声明在访问令牌里），
/// 高熵也意味着不需要慢哈希，SHA-256 足够且让查找可以直接走唯一索引。
/// </para>
/// </remarks>
public sealed class RefreshToken
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>令牌明文的 SHA-256 哈希（Base64）。</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>所属账号主键。</summary>
    public int UserId { get; set; }

    /// <summary>签发时刻（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>到期时刻（UTC）。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>撤销时刻（UTC）。刷新时轮换旧令牌、登出时主动撤销，都会写入此字段。</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// 审计记录。
/// </summary>
/// <remarks>
/// 只增不改：没有任何代码路径会更新或删除本表的数据。
/// </remarks>
public sealed class AuditLog
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>发生时刻（UTC）。</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>动作标识，如 <c>auth.login</c>。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>操作者账号名；登录失败时为尝试使用的用户名。</summary>
    public string? ActorName { get; set; }

    /// <summary>操作对象的类别。</summary>
    public string? TargetType { get; set; }

    /// <summary>操作对象的标识。</summary>
    public string? TargetId { get; set; }

    /// <summary>补充说明；失败时为失败原因。不得写入凭据。</summary>
    public string? Detail { get; set; }

    /// <summary>来源 IP。</summary>
    public string? RemoteIp { get; set; }

    /// <summary>本次操作是否成功。</summary>
    public bool Success { get; set; }
}
