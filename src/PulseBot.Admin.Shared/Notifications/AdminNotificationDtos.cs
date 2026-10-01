namespace PulseBot.Admin.Shared.Notifications;

/// <summary>一条转发到后台的日志记录。</summary>
/// <remarks>
/// 只保留展示必需的字段：日志的原始状态对象（<c>TState</c>）类型任意，
/// 序列化它既可能失败也可能泄露内部结构，因此在这里就完成格式化，
/// 出站的一律是已经定型为字符串的文本。
/// </remarks>
/// <param name="Timestamp">记录时刻（UTC）。</param>
/// <param name="Level">级别名（<c>Information</c>、<c>Warning</c> 等），与 <c>LogLevel</c> 枚举同名。</param>
/// <param name="Category">日志类别，通常是产生该日志的类型全名。</param>
/// <param name="Message">已格式化好的正文；若带异常，异常文本已包含在内。</param>
public sealed record AdminLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message);

/// <summary>一条转发到后台的事件摘要。</summary>
/// <remarks>
/// <para>
/// 刻意只传摘要而不是完整事件：事件对象带有回复通道（<see cref="object"/> 层面的发送器），
/// 序列化它会连带把适配器内部结构暴露出去，且体积不可控。
/// </para>
/// <para>
/// 类型字段用稳定的字面量（<c>message</c> / <c>notice</c> / <c>request</c>）而不是 CLR 类型名：
/// 前者是后台与前端的约定，改名不会影响；后者一改就是一次静默的前端故障。
/// 未知类型回退为 CLR 类型名，至少不会丢信息。
/// </para>
/// </remarks>
/// <param name="OccurredAt">事件进入管道并被抓取的时刻（UTC）。</param>
/// <param name="Type">事件类型字面量。</param>
/// <param name="SenderId">发送者 id；该类型事件没有发送者概念时为 null。</param>
/// <param name="Conversation">会话标识（<c>平台/类型/id</c>）；不涉及会话的事件为 null。</param>
/// <param name="Preview">正文预览，已按上限截断；无正文时为 null。</param>
public sealed record AdminEventEntry(
    DateTimeOffset OccurredAt,
    string Type,
    string? SenderId,
    string? Conversation,
    string? Preview);
