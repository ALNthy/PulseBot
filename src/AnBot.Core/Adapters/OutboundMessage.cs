using AnBot.Core.Messages;

namespace AnBot.Core.Adapters;

/// <summary>
/// 出站消息：适配器发送一条消息所需的全部信息。
/// </summary>
/// <remarks>
/// 「主动发送」与「被动回复」的差异只用 <see cref="ReplyToMessageId"/> 一个字段表达：
/// 为 null 即主动发送；非 null 时适配器应优先使用平台原生回复能力，
/// 平台不支持时退化为在消息前插入 <see cref="ReplySegment"/>。
/// 这样差异由适配器按平台能力落地，插件侧只面对 IMessageSender 的两个语义清晰的方法。
/// </remarks>
public sealed record OutboundMessage
{
    /// <summary>目标会话。</summary>
    public required ConversationId Conversation { get; init; }

    /// <summary>消息内容。</summary>
    public required Message Message { get; init; }

    /// <summary>被动回复时填写被回复消息的平台内 id；主动发送时为 null。</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>是否为被动回复。</summary>
    public bool IsReply => ReplyToMessageId is not null;
}
