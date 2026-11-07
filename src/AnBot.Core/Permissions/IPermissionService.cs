using AnBot.Core.Events;

namespace AnBot.Core.Permissions;

/// <summary>
/// 权限服务：面向插件与框架的权限判定与授权管理入口。
/// </summary>
/// <remarks>
/// <para>
/// 判定遵循「就近合并」：一次消息会话中，用户级、群级、全局三级授权**任一满足**即视为拥有该节点；
/// 另外超级管理员（<see cref="PermissionOptions.SuperAdmins"/>）拥有全部权限。
/// </para>
/// <para>
/// 授予/撤销接口面向宿主与后台管理；插件若需要「只有管理员能用」这类判断，
/// 应在自己的命令上标注 <see cref="RequirePermissionAttribute"/>，
/// 或在业务逻辑里注入本接口调用 <see cref="IsGrantedAsync(MessageEvent, PermissionRequirement, CancellationToken)"/>。
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// 权限不足时向用户展示的提示文本；为空字符串表示静默拒绝（仅记日志）。
    /// 由命令分发器在拦截命令时使用。
    /// </summary>
    string DeniedMessage { get; }

    /// <summary>
    /// 判定某条消息的发送者是否满足权限要求。
    /// </summary>
    /// <param name="message">触发本次判定的消息事件（发送者与所在会话取自它）。</param>
    /// <param name="requirement">权限要求；<see cref="PermissionRequirement.IsEmpty"/> 为真时一律通过。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<bool> IsGrantedAsync(
        MessageEvent message,
        PermissionRequirement requirement,
        CancellationToken cancellationToken);

    /// <summary>
    /// 判定某个主体是否被授予了指定权限节点（不做会话上下文推断，适用于后台管理界面）。
    /// </summary>
    /// <param name="subject">授权主体。</param>
    /// <param name="node">权限节点，或通配节点 <c>*</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<bool> IsGrantedAsync(
        PermissionSubject subject,
        string node,
        CancellationToken cancellationToken);

    /// <summary>给主体授予一个权限节点（重复授予是幂等的）。</summary>
    /// <param name="subject">授权主体。</param>
    /// <param name="node">权限节点，或通配节点 <c>*</c>（授予后者等于授予全部权限）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask GrantAsync(PermissionSubject subject, string node, CancellationToken cancellationToken);

    /// <summary>撤销主体已获得的某个权限节点（撤销不存在的授权是幂等的）。</summary>
    /// <param name="subject">授权主体。</param>
    /// <param name="node">权限节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RevokeAsync(PermissionSubject subject, string node, CancellationToken cancellationToken);

    /// <summary>列出主体当前拥有的全部权限节点（供后台管理与诊断展示）。</summary>
    /// <param name="subject">授权主体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyCollection<string>> ListAsync(PermissionSubject subject, CancellationToken cancellationToken);
}
