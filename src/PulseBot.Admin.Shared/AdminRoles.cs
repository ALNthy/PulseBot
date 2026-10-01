namespace PulseBot.Admin.Shared;

/// <summary>
/// 后台角色名。
/// </summary>
/// <remarks>
/// 第一期只有一种角色：能登录后台就能读全部监控数据。
/// 更细的权限划分（只读运维、审计员等）留给后续步骤，届时这里的常量会扩展成一张角色表。
/// </remarks>
public static class AdminRoles
{
    /// <summary>管理员：可访问后台的全部接口。</summary>
    public const string Admin = "admin";
}
