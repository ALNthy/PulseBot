namespace PulseBot.Core.Permissions;

/// <summary>
/// 声明式权限标注：标在带 <c>[OnCommand]</c> 的方法上，表示调用该命令需要哪些权限。
/// </summary>
/// <remarks>
/// <para>用法：</para>
/// <code>
/// [OnCommand("ban")]
/// [RequirePermission("admin.ban")]                                    // 需要 admin.ban
/// public ValueTask BanAsync(CommandContext context, CancellationToken cancellationToken) { ... }
///
/// [OnCommand("mute")]
/// [RequirePermission("admin.mute", "admin.ban", Mode = PermissionRequirementMode.Any)]  // 两者任有其一
/// public ValueTask MuteAsync(CommandContext context, CancellationToken cancellationToken) { ... }
/// </code>
/// <para>
/// 未标注本属性的命令不做任何权限检查（默认放行）；本属性也适用于非命令方法，
/// 插件可在自己的逻辑里读取它并调用 <see cref="IPermissionService"/> 自行判定。
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequirePermissionAttribute : Attribute
{
    /// <summary>声明所需的权限节点。</summary>
    /// <param name="nodes">至少一个权限节点（如 <c>admin.ban</c>）。</param>
    /// <exception cref="ArgumentException">未提供节点，或节点为空/含空白字符。</exception>
    public RequirePermissionAttribute(params string[] nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Length == 0)
        {
            throw new ArgumentException("必须至少声明一个权限节点。", nameof(nodes));
        }

        foreach (var node in nodes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(node);

            if (node.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"权限节点「{node}」不能包含空白字符。", nameof(nodes));
            }
        }

        Nodes = [.. nodes];
    }

    /// <summary>所需的权限节点。</summary>
    public IReadOnlyList<string> Nodes { get; }

    /// <summary>
    /// 节点之间的组合方式，默认 <see cref="PermissionRequirementMode.All"/>（全部满足）。
    /// 若要表达「有其中任意一个即可」，显式设为 <see cref="PermissionRequirementMode.Any"/>。
    /// </summary>
    public PermissionRequirementMode Mode { get; init; } = PermissionRequirementMode.All;

    /// <summary>转换为框架内部使用的要求对象。</summary>
    public PermissionRequirement ToRequirement() => new(Mode, Nodes);
}
