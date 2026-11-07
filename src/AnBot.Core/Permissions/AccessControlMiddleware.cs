using Microsoft.Extensions.Logging;
using AnBot.Core.Dispatching;
using AnBot.Core.Events;
using AnBot.Core.Messages;

namespace AnBot.Core.Permissions;

/// <summary>
/// 接入控制中间件：位于事件管道的**最外层**做黑/白名单过滤，
/// 被拦截的消息不调用续延，因此不会到达会话拦截、命令分发或任何插件。
/// </summary>
/// <remarks>
/// <para>判定规则（自上而下，先命中先返回）：</para>
/// <list type="number">
///   <item><description>发送者在用户黑名单中 → 拦截。</description></item>
///   <item><description>所在群在群黑名单中 → 拦截（私聊无群身份，跳过）。</description></item>
///   <item><description>发送者是超级管理员 → 放行，不再检查白名单（避免把自己锁在门外）。</description></item>
///   <item><description>用户白名单非空且发送者不在其中 → 拦截。</description></item>
///   <item><description>群白名单非空且所在群不在其中 → 拦截（私聊不受影响）。</description></item>
/// </list>
/// <para>
/// 黑名单优先于白名单且对所有人（含超级管理员）生效：被拉黑的人就是不能用，
/// 这与「超管绕过权限节点检查」是两件独立的事。
/// </para>
/// <para>
/// 名单不缓存在本类型里：每次判定从 <see cref="IAccessPolicy.Current"/> 取一份快照，
/// 该操作只是一次引用读。因此后台更新名单后，下一条消息就按新规则判定，
/// 既不需要重启宿主，也不需要重建中间件实例。
/// </para>
/// </remarks>
public sealed class AccessControlMiddleware : MessageEventMiddleware
{
    private readonly IAccessPolicy _policy;
    private readonly ILogger<AccessControlMiddleware> _logger;

    /// <summary>创建接入控制中间件。</summary>
    /// <param name="policy">接入策略持有者（提供当前生效的黑白名单与超管名单）。</param>
    /// <param name="logger">结构化日志。</param>
    public AccessControlMiddleware(IAccessPolicy policy, ILogger<AccessControlMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// 取 <see cref="int.MaxValue"/>：接入控制是事件管道的最外层，
    /// 必须**先于**会话拦截（<c>SessionPromptMiddleware</c> 取 <c>int.MaxValue - 1</c>）
    /// 与命令分发（<c>CommandMiddleware</c> 取 <c>int.MaxValue - 2</c>）执行，
    /// 否则被拉黑的人仍能与机器人对话或触发命令。
    /// 三档取值互不相同，因此顺序只由优先级数值决定，与 DI 注册顺序无关。
    /// </summary>
    public override int Priority => int.MaxValue;

    /// <inheritdoc />
    public override ValueTask InvokeAsync(
        MessageEvent @event,
        EventContext context,
        EventMiddlewareNext next,
        CancellationToken cancellationToken)
    {
        var reason = GetBlockReason(@event);
        if (reason is null)
        {
            return next(@event, context, cancellationToken);
        }

        _logger.LogWarning(
            "已拦截一条消息：{Reason}。发送者 {SenderId}，会话 {Conversation}，消息 {MessageId}",
            reason,
            @event.SenderId,
            @event.Conversation,
            @event.MessageId);

        // 终止管道：被拦截的消息对会话拦截、命令分发与所有插件都不存在。
        return ValueTask.CompletedTask;
    }

    /// <summary>返回拦截原因；放行时返回 <c>null</c>。</summary>
    private string? GetBlockReason(MessageEvent message)
    {
        // 每次判定读一次当前快照：名单可被后台热更新（整体替换），
        // 这里取到的一定是自洽的一份，不会读到「改了一半」的中间状态。
        AccessPolicySnapshot policy = _policy.Current;

        if (policy.UserBlacklist.Contains(message.SenderId))
        {
            return "发送者在用户黑名单中";
        }

        var isGroupConversation = message.Conversation.Type != ConversationType.Private;
        if (isGroupConversation && policy.GroupBlacklist.Contains(message.Conversation.Id))
        {
            return "所在群在群黑名单中";
        }

        // 超级管理员不受白名单限制：白名单是「只允许名单内的人使用」，
        // 若把管理员自己漏加就会导致机器人无法管理，因此这里直接放行。
        if (policy.SuperAdmins.Contains(message.SenderId))
        {
            return null;
        }

        // 白名单为空 = 不限制；非空才构成允许列表。
        if (policy.UserWhitelist.Count > 0 && !policy.UserWhitelist.Contains(message.SenderId))
        {
            return "发送者不在用户白名单中";
        }

        if (isGroupConversation
            && policy.GroupWhitelist.Count > 0
            && !policy.GroupWhitelist.Contains(message.Conversation.Id))
        {
            return "所在群不在群白名单中";
        }

        return null;
    }
}
