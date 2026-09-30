namespace PulseBot.Core.Permissions;

/// <summary>
/// 授权主体类别：权限可以授予到「某个用户」「某个群」或「全局」三个粒度。
/// </summary>
public enum PermissionSubjectKind
{
    /// <summary>用户级：权限跟随用户本人，在该用户出现的任意会话中生效。</summary>
    User,

    /// <summary>群级：权限只在该群（或频道）的会话中生效，对私聊无意义。</summary>
    Group,

    /// <summary>全局级：所有会话中的所有用户都拥有。</summary>
    Global,
}

/// <summary>
/// 授权主体：权限授予与检查的最小单位。
/// </summary>
/// <remarks>
/// <para>
/// 用私有构造 + 静态工厂而非公开构造：主体是权限表的键，非法状态（空 id 的用户、带 id 的全局）
/// 一旦流入字典就很难排查，因此在入口处一律拒绝。
/// </para>
/// <para>
/// 本类型是 record，相等性按值比较，可直接作为字典键使用。
/// </para>
/// </remarks>
public sealed record PermissionSubject
{
    /// <summary>全局主体：授予它的权限对所有会话的所有用户生效。</summary>
    public static PermissionSubject Global { get; } = new(PermissionSubjectKind.Global, string.Empty);

    private PermissionSubject(PermissionSubjectKind kind, string id)
    {
        Kind = kind;
        Id = id;
    }

    /// <summary>主体类别。</summary>
    public PermissionSubjectKind Kind { get; }

    /// <summary>主体标识：用户 id 或群 id；全局主体为空字符串。</summary>
    public string Id { get; }

    /// <summary>创建用户主体。</summary>
    /// <param name="userId">平台内的用户 id（如 QQ 号）。</param>
    public static PermissionSubject User(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new PermissionSubject(PermissionSubjectKind.User, userId);
    }

    /// <summary>创建群主体。</summary>
    /// <param name="groupId">平台内的群 id。</param>
    public static PermissionSubject Group(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return new PermissionSubject(PermissionSubjectKind.Group, groupId);
    }

    /// <summary>面向日志的显示文本，如 <c>用户:12345</c>、<c>群:67890</c>、<c>全局</c>。</summary>
    public override string ToString() => Kind switch
    {
        PermissionSubjectKind.User => $"用户:{Id}",
        PermissionSubjectKind.Group => $"群:{Id}",
        _ => "全局",
    };
}
