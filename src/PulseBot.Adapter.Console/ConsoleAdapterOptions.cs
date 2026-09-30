namespace PulseBot.Adapter.Console;

/// <summary>
/// 控制台适配器的配置。
/// </summary>
public sealed class ConsoleAdapterOptions
{
    /// <summary>配置节名（appsettings.json 中为 <c>"Adapters:Console"</c>）。</summary>
    public const string SectionName = "Adapters:Console";

    /// <summary>
    /// 控制台用户在该平台内的 id，作为每条消息事件的 SenderId。
    /// </summary>
    /// <remarks>
    /// 权限系统按 SenderId 判定用户级授权与超级管理员，因此把这个值填进
    /// <c>Permissions:SuperAdmins</c>，就能在控制台里直接测试受 <c>[RequirePermission]</c>
    /// 保护的命令。
    /// </remarks>
    public string UserId { get; set; } = "console-user";

    /// <summary>发送者展示名；仅用于日志与提示，不参与任何判定。</summary>
    public string UserDisplayName { get; set; } = "控制台用户";

    /// <summary>输入提示符。</summary>
    public string Prompt { get; set; } = "你> ";

    /// <summary>机器人输出的前缀，用于把回复与用户输入区分开。</summary>
    public string BotPrefix { get; set; } = "机器人> ";
}
