using Microsoft.Extensions.Options;
using PulseBot.Admin.Shared;
using PulseBot.Admin.Shared.Notifications;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;

namespace PulseBot.Admin.Api.Notifications;

/// <summary>
/// 把每条进入管道的事件复制一份摘要投递给后台的实时流。
/// </summary>
/// <remarks>
/// <para>
/// 挂在事件管道的<b>中间件</b>上而不是事件处理器上：处理器要按事件类型逐个注册，
/// 新事件类型出现时就得记得补一处，而漏掉的症状是「后台看不到这类事件」——
/// 恰恰是排查问题时最需要它的时候。中间件会收到所有类型，一次覆盖。
/// </para>
/// <para>
/// 与接入控制、命令分发的关系见 <see cref="Priority"/>。
/// </para>
/// </remarks>
public sealed class AdminEventMirror : IEventMiddleware
{
    /// <summary>正文预览的字符上限。</summary>
    /// <remarks>
    /// 后台列表只用来扫一眼「谁在说什么」，完整正文既无人看也白白撑大推送体积；
    /// 而且长消息乘以高频事件足以让有界通道长期处于丢弃状态。
    /// </remarks>
    private const int PreviewLength = 200;

    private readonly AdminNotifications _notifications;
    private readonly TimeProvider _time;
    private readonly bool _enabled;

    /// <summary>构造函数注入。</summary>
    /// <param name="notifications">推送缓冲。</param>
    /// <param name="options">后台配置：关闭后台时完全不复制。</param>
    /// <param name="time">时间源。</param>
    public AdminEventMirror(
        AdminNotifications notifications,
        IOptions<AdminOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _notifications = notifications;
        _time = time;
        _enabled = options.Value.Enabled;
    }

    /// <summary>
    /// 落在接入控制与命令分发之间的那一档。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须比 <c>CommandMiddleware</c>（<c>int.MaxValue - 2</c>）更外层：命令消息在命令
    /// 分发之后不再向内传递，放在内层就完全看不到用户发的命令——而那正是后台最想看的流量。
    /// </para>
    /// <para>
    /// 又必须比 <c>AccessControlMiddleware</c>（<c>int.MaxValue</c>）更内层：被黑白名单
    /// 拦下的消息不该出现在事件流里。它们不会因此消失在后台——接入控制拦截时会记一条
    /// 带原因的日志，那条日志经 <see cref="AdminLogBroadcaster"/> 出现在日志流中。
    /// </para>
    /// <para>
    /// 两者之间只剩这一档，与 <c>SessionPromptMiddleware</c> 同级。同级时的先后由注册顺序
    /// 决定，而本中间件只是旁路复制一条通知，既不改写事件也不短路，因此无论落在会话拦截的
    /// 外层还是内层，行为都一致——唯一的差别是「被会话提示消费掉的输入」是否出现在事件流里。
    /// </para>
    /// </remarks>
    public int Priority => int.MaxValue - 1;

    /// <inheritdoc />
    public ValueTask InvokeAsync(
        IEvent @event,
        EventContext context,
        EventMiddlewareNext next,
        CancellationToken cancellationToken)
    {
        if (_enabled)
        {
            _notifications.Publish(Describe(@event));
        }

        // 只旁路复制：不改写事件、不短路、不做后处理，因此直接续延。
        return next(@event, context, cancellationToken);
    }

    /// <summary>把事件压成一条摘要。</summary>
    private AdminEventEntry Describe(IEvent @event) => @event switch
    {
        MessageEvent message => new AdminEventEntry(
            OccurredAt: _time.GetUtcNow(),
            Type: "message",
            SenderId: message.SenderId,
            Conversation: message.Conversation.ToString(),
            Preview: Truncate(message.PlainText)),

        // 通知事件没有「发送者」：OperatorId 是执行该操作的管理员，平台未提供时为 null。
        NoticeEvent notice => new AdminEventEntry(
            OccurredAt: _time.GetUtcNow(),
            Type: "notice",
            SenderId: notice.OperatorId,
            Conversation: notice.Conversation.ToString(),
            Preview: notice.RawType),

        // 请求事件不在某个会话里发生，它是「某人申请加入某群/加好友」，
        // 群号由 GroupId 单独表达，因此这里不填会话。
        RequestEvent request => new AdminEventEntry(
            OccurredAt: _time.GetUtcNow(),
            Type: "request",
            SenderId: request.RequesterId,
            Conversation: null,
            Preview: Truncate(request.Comment)),

        // 未知类型：回退为 CLR 类型名而不是丢弃。以后新增事件类型时，
        // 后台至少能看到「有这么一类事件在流动」，而不是无声无息。
        _ => new AdminEventEntry(
            OccurredAt: _time.GetUtcNow(),
            Type: @event.GetType().Name,
            SenderId: null,
            Conversation: null,
            Preview: null),
    };

    private static string? Truncate(string? text)
        => text is null || text.Length <= PreviewLength
            ? text
            : string.Concat(text.AsSpan(0, PreviewLength), "…");
}
