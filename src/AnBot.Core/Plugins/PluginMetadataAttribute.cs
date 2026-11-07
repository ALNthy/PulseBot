namespace AnBot.Core.Plugins;

/// <summary>
/// 插件元数据：声明名称、版本、描述与依赖，供日志、帮助信息与后台管理展示。
/// </summary>
/// <example>
/// <code>
/// [PluginMetadata("Echo", Version = "0.1.0", Description = "示例插件：复读消息")]
/// public sealed class EchoPlugin : IPlugin { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginMetadataAttribute : Attribute
{
    /// <summary>
    /// 创建插件元数据。
    /// </summary>
    /// <param name="name">插件显示名称（用于日志与后续的依赖解析）。</param>
    /// <exception cref="ArgumentException">名称为空时抛出。</exception>
    public PluginMetadataAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>插件显示名称（唯一标识）。</summary>
    public string Name { get; }

    /// <summary>插件版本（语义化版本字符串）。</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>插件描述。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 依赖的其它插件名称列表。
    /// 本步骤仅承载元数据，不做依赖解析与加载排序（留待插件系统扩展时实现）。
    /// </summary>
    public string[] Dependencies { get; init; } = [];
}
