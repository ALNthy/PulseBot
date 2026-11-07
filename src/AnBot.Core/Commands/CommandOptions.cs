namespace AnBot.Core.Commands;

/// <summary>
/// 命令分发配置。宿主绑定配置节后通过 <c>IOptions&lt;CommandOptions&gt;</c> 注入使用；
/// 未绑定时使用本类默认值。
/// </summary>
public sealed class CommandOptions
{
    /// <summary>推荐配置节名，宿主形如 <c>services.Configure&lt;CommandOptions&gt;(config.GetSection(CommandOptions.SectionName))</c>。</summary>
    public const string SectionName = "Commands";

    /// <summary>
    /// 命令前缀，默认 <c>"/"</c>（消息形如 <c>/echo 你好</c>）。
    /// 配置为空字符串时进入「全无前缀」模式：所有命令都直接以命令名开头匹配。
    /// </summary>
    public string Prefix { get; set; } = "/";

    /// <summary>
    /// 是否允许无前缀命令，默认允许（中文机器人惯例：直接发「天气 北京」）。
    /// 无前缀匹配只认「消息首个空白分隔片段与命令名精确相等」，普通聊天不会被误判成命令。
    /// 若希望所有命令都必须带前缀，置为 <c>false</c>。
    /// </summary>
    public bool EnablePrefixlessCommands { get; set; } = true;

    /// <summary>
    /// 命令名匹配是否区分大小写，默认不区分（OrdinalIgnoreCase）。
    /// 对中文等非 ASCII 命令名没有影响。
    /// </summary>
    public bool CaseSensitive { get; set; }
}
