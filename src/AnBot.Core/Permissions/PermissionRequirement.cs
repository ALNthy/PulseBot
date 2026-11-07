namespace AnBot.Core.Permissions;

/// <summary>多个权限节点之间的组合方式。</summary>
public enum PermissionRequirementMode
{
    /// <summary>全部节点都必须满足（AND）。</summary>
    All,

    /// <summary>满足其中任一节点即可（OR）。</summary>
    Any,
}

/// <summary>
/// 一条命令（或功能）的权限要求：一组权限节点 + 它们之间的组合方式。
/// </summary>
/// <remarks>
/// <para>
/// 空要求（<see cref="IsEmpty"/>）表示「不限制」——按默认放行策略，未声明要求的命令所有人都能用。
/// </para>
/// <para>
/// 节点是纯字符串标识（如 <c>admin.ban</c>），由插件自行约定命名；框架只做相等比较，
/// 额外认一个通配节点 <c>*</c>（拥有它即拥有全部权限，供超级管理员使用）。
/// </para>
/// </remarks>
public sealed class PermissionRequirement
{
    /// <summary>空要求：不限制任何权限。</summary>
    public static PermissionRequirement None { get; } = new(PermissionRequirementMode.All, []);

    /// <summary>创建一个权限要求。</summary>
    /// <param name="mode">节点之间的组合方式。</param>
    /// <param name="nodes">权限节点列表；为空表示不限制。</param>
    /// <exception cref="ArgumentException">存在空白的权限节点。</exception>
    public PermissionRequirement(PermissionRequirementMode mode, IReadOnlyCollection<string> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // 节点是权限表的键，空白节点永远不会被授予成功，属于配置错误，在构造期就拒绝。
        foreach (var node in nodes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(node);
            if (node.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"权限节点「{node}」不能包含空白字符。", nameof(nodes));
            }
        }

        Mode = mode;
        Nodes = [.. nodes];
    }

    /// <summary>节点之间的组合方式。</summary>
    public PermissionRequirementMode Mode { get; }

    /// <summary>权限节点列表（构造时已复制，外部不可变）。</summary>
    public IReadOnlyList<string> Nodes { get; }

    /// <summary>是否没有任何权限要求（即不限制）。</summary>
    public bool IsEmpty => Nodes.Count == 0;
}
