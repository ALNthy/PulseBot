namespace PulseBot.Core.Permissions;

/// <summary>
/// 权限与接入控制配置。宿主绑定配置节后通过 <c>IOptions&lt;PermissionOptions&gt;</c> 注入；
/// 未绑定时使用本类默认值（无超级管理员、无黑白名单、权限不足时回复默认提示）。
/// </summary>
/// <remarks>
/// <para>宿主的推荐用法（appsettings.json）：</para>
/// <code>
/// {
///   "Permissions": {
///     "SuperAdmins": [ "123456789" ],
///     "DeniedMessage": "你没有权限执行该命令。",
///     "UserBlacklist": [ "987654321" ],
///     "GroupBlacklist": [ "111222333" ]
///   }
/// }
/// </code>
/// </remarks>
public sealed class PermissionOptions
{
    /// <summary>推荐配置节名。</summary>
    public const string SectionName = "Permissions";

    /// <summary>
    /// 超级管理员用户 id 列表：拥有全部权限（含通配节点 <c>*</c>），无需逐条授予。
    /// 同时不受白名单限制（避免把自己锁在门外）；黑名单对所有人生效，超管也不例外。
    /// </summary>
    public string[] SuperAdmins { get; set; } = [];

    /// <summary>
    /// 权限不足时向用户回复的提示文本。设为空字符串则静默拒绝（只记日志）。
    /// </summary>
    public string DeniedMessage { get; set; } = "你没有权限执行该命令。";

    /// <summary>用户黑名单：名单内的用户发送的消息一律被丢弃，优先级高于白名单。</summary>
    public string[] UserBlacklist { get; set; } = [];

    /// <summary>
    /// 用户白名单：**非空时**构成允许列表，只有名单内的用户能使用机器人；
    /// 留空表示不限制。超级管理员不受本名单限制。
    /// </summary>
    public string[] UserWhitelist { get; set; } = [];

    /// <summary>群黑名单：名单内的群消息一律被丢弃，优先级高于白名单。私聊不受影响。</summary>
    public string[] GroupBlacklist { get; set; } = [];

    /// <summary>
    /// 群白名单：**非空时**只有在名单内的群能使用机器人；留空表示不限制。
    /// 私聊会话不受本名单影响。
    /// </summary>
    public string[] GroupWhitelist { get; set; } = [];
}
