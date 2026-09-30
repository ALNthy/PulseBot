namespace PulseBot.Core.Messages;

/// <summary>文本段：最小、所有平台都必然支持的段类型，也是降级回退的首选形态。</summary>
/// <param name="Text">文本内容；允许空串。</param>
public sealed record TextSegment(string Text) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => Text;
}

/// <summary>图片段：只保存「图片来源」，实际加载 / 上传由适配器完成。</summary>
/// <param name="Source">
/// 图片来源标识：http(s) URL、本地绝对路径或平台 file 标识（如 OneBot 的 file/url）。
/// 该字符串的解释权归各适配器，Core 与插件不得解析其内容。
/// </param>
public sealed record ImageSegment(string Source) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => "[图片]";
}

/// <summary>
/// @ 提及段。目标 id 的平台归属由所在消息的会话平台（ConversationId.Platform）隐含，
/// 段内不再重复携带平台名，避免同一消息出现两份可能不一致的平台信息。
/// </summary>
/// <param name="TargetId">被提及用户的平台内 id（如 QQ 号 / openid）。</param>
/// <param name="DisplayName">展示名，可为 null；仅用于纯文本回退，不参与平台协议。</param>
public sealed record AtSegment(string TargetId, string? DisplayName = null) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => string.IsNullOrEmpty(DisplayName) ? $"@{TargetId}" : $"@{DisplayName}";
}

/// <summary>
/// 回复引用段：声明本消息是对某条消息的回复，用于在消息体内部表达引用关系
/// （平台支持原生回复时优先走 OutboundMessage.ReplyToMessageId）。
/// </summary>
/// <param name="MessageId">被引用消息在该平台内的 id。</param>
public sealed record ReplySegment(string MessageId) : MessageSegment
{
    /// <summary>回复引用本身不产生可见文本。</summary>
    public override string ToPlainText() => string.Empty;
}

/// <summary>表情段（平台内置表情，如 QQ 表情）。</summary>
/// <param name="FaceId">平台表情编号；不同平台含义不同，由适配器负责映射。</param>
public sealed record FaceSegment(string FaceId) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => "[表情]";
}
