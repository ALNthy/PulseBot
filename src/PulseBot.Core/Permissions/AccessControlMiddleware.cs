using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Core.Permissions;

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
/// 名单是静态配置，进程内不提供运行时增删接口；后续若需要动态封禁，
/// 应由后台管理把名单变更落库并驱动宿主重建配置。
/// </para>
/// </remarks>
public sealed class AccessControlMiddleware : MessageEventMiddleware
{
    private readonly FrozenSet<string> _userBlacklist;
    private readonly FrozenSet<string> _userWhitelist;
    private readonly FrozenSet<string> _groupBlacklist;
    private readonly FrozenSet<string> _groupWhitelist;
    private readonly FrozenSet<string> _superAdmins;
    private readonly ILogger<AccessControlMiddleware> _logger;

    /// <summary>创建接入控制中间件。</summary>
    /// <param name="options">权限配置（黑白名单与超级管理员名单）。</param>
    /// <param name="logger">结构化日志。</param>
    public AccessControlMiddleware(IOptions<PermissionOptions> options, ILogger<AccessControlMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var value = options.Value;

        // 四份名单在构造时冻结：每条消息都要至少查一次，这里换最省的查找结构。
        _userBlacklist = value.UserBlacklist.ToFrozenSet(StringComparer.Ordinal);
        _userWhitelist = value.UserWhitelist.ToFrozenSet(StringComparer.Ordinal);
        _groupBlacklist = value.GroupBlacklist.ToFrozenSet(StringComparer.Ordinal);
        _groupWhitelist = value.GroupWhitelist.ToFrozenSet(StringComparer.Ordinal);
        _superAdmins = value.SuperAdmins.ToFrozenSet(StringComparer.Ordinal);
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
        if (_userBlacklist.Contains(message.SenderId))
        {
            return "发送者在用户黑名单中";
        }

        var isGroupConversation = message.Conversation.Type != ConversationType.Private;
        if (isGroupConversation && _groupBlacklist.Contains(message.Conversation.Id))
        {
            return "所在群在群黑名单中";
        }

        // 超级管理员不受白名单限制：白名单是「只允许名单内的人使用」，
        // 若把管理员自己漏加就会导致机器人无法管理，因此这里直接放行。
        if (_superAdmins.Contains(message.SenderId))
        {
            return null;
        }

        // 白名单为空 = 不限制；非空才构成允许列表。
        if (_userWhitelist.Count > 0 && !_userWhitelist.Contains(message.SenderId))
        {
            return "发送者不在用户白名单中";
        }

        if (isGroupConversation
            && _groupWhitelist.Count > 0
            && !_groupWhitelist.Contains(message.Conversation.Id))
        {
            return "所在群不在群白名单中";
        }

        return null;
    }
}
