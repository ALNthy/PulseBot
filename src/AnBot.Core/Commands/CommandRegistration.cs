using AnBot.Core.Permissions;

namespace AnBot.Core.Commands;

/// <summary>
/// 命令表中的一条内部注册记录（Core 内部使用，不对插件公开）。
/// 每个可触发的命令名（主名或别名）对应一条独立记录，冲突裁决也以「名字」为粒度；
/// 记录持有启动时由 <c>CreateDelegate</c> 编译出的强类型委托：
/// 运行期直接调用 <see cref="Handler"/>，不触碰任何反射。
/// </summary>
internal sealed class CommandRegistration
{
    /// <summary>本条记录对应的触发名（主命令名或别名，均不含前缀）。</summary>
    public required string TriggerName { get; init; }

    /// <summary>声明该命令时使用的主命令名，用于日志与诊断展示。</summary>
    public required string PrimaryName { get; init; }

    /// <summary>命令名冲突时的裁决优先级，数值大者胜。</summary>
    public required int Priority { get; init; }

    /// <summary>注册该命令的插件类型显示名，仅用于日志与诊断。</summary>
    public required string PluginName { get; init; }

    /// <summary>命令说明（可能为空）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 该命令声明的权限要求；未标注 <c>[RequirePermission]</c> 时为 <c>null</c>，
    /// 按默认放行策略不做任何检查。启动扫描时构造一次并缓存，运行期只做判定。
    /// </summary>
    public PermissionRequirement? Requirement { get; init; }

    /// <summary>编译后的强类型命令处理器委托（启动时绑定一次，运行时零反射）。</summary>
    public required CommandHandler Handler { get; init; }
}
