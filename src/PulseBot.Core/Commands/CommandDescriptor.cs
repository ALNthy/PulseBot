namespace PulseBot.Core.Commands;

/// <summary>
/// 命令表条目的只读快照：每个当前可触发的命令名（主名或别名）一条记录，
/// 供日志、帮助命令与后台管理展示。仅供诊断用途，不在命令匹配热路径上使用。
/// </summary>
/// <param name="Name">当前可触发的命令名（主名或别名，不含前缀）。</param>
/// <param name="PrimaryName">声明该命令时使用的主命令名；当 <paramref name="Name"/> 是别名时可据此找到主名。</param>
/// <param name="PluginName">注册该命令的插件类型名。</param>
/// <param name="Description">命令说明（可能为空）。</param>
/// <param name="Priority">命令名冲突时的裁决优先级。</param>
public sealed record CommandDescriptor(
    string Name,
    string PrimaryName,
    string PluginName,
    string? Description,
    int Priority);
