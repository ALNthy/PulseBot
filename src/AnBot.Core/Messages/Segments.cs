namespace AnBot.Core.Messages;

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

/// <summary>
/// @ 全体成员段。与 <see cref="AtSegment"/> 分开：它的目标不是某个具体用户，
/// 而是整个会话的全体成员，用一个虚构的用户 id 表达会污染 id 空间。
/// </summary>
public sealed record MentionAllSegment : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => "@全体成员";
}

/// <summary>
/// 音频段：语音消息与音频文件共用同一种段——两者对 Core 与插件都是「一段可播放的音频」，
/// 区分只对平台有意义（如 OneBot 12 的 voice 与 audio），由适配器在映射时决定归入哪个段。
/// </summary>
/// <param name="Source">音频来源标识，解释权归适配器（约定同 <see cref="ImageSegment.Source"/>）。</param>
public sealed record AudioSegment(string Source) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => "[语音]";
}

/// <summary>视频段。</summary>
/// <param name="Source">视频来源标识，解释权归适配器。</param>
public sealed record VideoSegment(string Source) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => "[视频]";
}

/// <summary>文件段。</summary>
/// <param name="Source">文件来源标识，解释权归适配器。</param>
/// <param name="FileName">展示用文件名，可为 null（平台未提供时）。</param>
public sealed record FileSegment(string Source, string? FileName = null) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => string.IsNullOrEmpty(FileName) ? "[文件]" : $"[文件:{FileName}]";
}

/// <summary>位置段。</summary>
/// <param name="Latitude">纬度。</param>
/// <param name="Longitude">经度。</param>
/// <param name="Title">地点名称，可为 null。</param>
public sealed record LocationSegment(double Latitude, double Longitude, string? Title = null) : MessageSegment
{
    /// <inheritdoc />
    public override string ToPlainText() => string.IsNullOrEmpty(Title) ? "[位置]" : $"[位置:{Title}]";
}
