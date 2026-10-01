using PulseBot.Core.Messages;

namespace PulseBot.Adapter.OneBot.V12.Protocol;

/// <summary>Core 消息与 OneBot 12 消息段之间的双向映射。</summary>
/// <remarks>
/// 映射是有损的：OneBot 12 没有表情段，Core 的 <see cref="FaceSegment"/> 与插件自定义的段类型
/// 出站时一律降级为纯文本段（<see cref="MessageSegment.ToPlainText"/>），至少保住可见信息；
/// 入站遇到未知段类型同理，降级为占位文本。
/// </remarks>
public static class OneBotMessageTranslator
{
    /// <summary>把 OneBot 消息段数组翻译为 Core 消息；段数组为 null 或空时返回空消息。</summary>
    public static Message ToCoreMessage(IReadOnlyList<OneBotSegmentDto>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return Message.Empty;
        }

        var translated = new MessageSegment[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            translated[i] = ToCoreSegment(segments[i]);
        }

        return new Message(translated);
    }

    /// <summary>把单个 OneBot 消息段翻译为 Core 消息段。</summary>
    public static MessageSegment ToCoreSegment(OneBotSegmentDto segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        OneBotSegmentDataDto data = segment.Data;

        return segment.Type switch
        {
            "text" => new TextSegment(data.Text ?? string.Empty),
            "mention" => new AtSegment(data.UserId ?? string.Empty),
            "mention_all" => new MentionAllSegment(),
            "image" => new ImageSegment(data.FileId ?? string.Empty),

            // voice 与 audio 在 OneBot 12 是两种段（现场录音 / 音频文件），Core 归为一种。
            "voice" or "audio" => new AudioSegment(data.FileId ?? string.Empty),

            "video" => new VideoSegment(data.FileId ?? string.Empty),
            "file" => new FileSegment(data.FileId ?? string.Empty, data.Name),
            "location" => new LocationSegment(data.Latitude ?? 0, data.Longitude ?? 0, data.Title),
            "reply" => new ReplySegment(data.MessageId ?? string.Empty),

            // 未知段（实现端扩展类型）降级为占位文本，避免整条消息被丢弃。
            _ => new TextSegment($"[不支持的消息段: {segment.Type}]"),
        };
    }

    /// <summary>把 Core 消息翻译为 OneBot 12 消息段数组。</summary>
    public static List<OneBotSegmentDto> ToOneBotSegments(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var segments = new List<OneBotSegmentDto>(message.Segments.Count);
        foreach (MessageSegment segment in message.Segments)
        {
            segments.Add(ToOneBotSegment(segment));
        }

        return segments;
    }

    /// <summary>把单个 Core 消息段翻译为 OneBot 12 消息段。</summary>
    public static OneBotSegmentDto ToOneBotSegment(MessageSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return segment switch
        {
            TextSegment text => Create("text", new OneBotSegmentDataDto { Text = text.Text }),
            AtSegment at => Create("mention", new OneBotSegmentDataDto { UserId = at.TargetId }),
            MentionAllSegment => Create("mention_all", new OneBotSegmentDataDto()),
            ImageSegment image => Create("image", new OneBotSegmentDataDto { FileId = image.Source }),
            AudioSegment audio => Create("voice", new OneBotSegmentDataDto { FileId = audio.Source }),
            VideoSegment video => Create("video", new OneBotSegmentDataDto { FileId = video.Source }),
            FileSegment file => Create("file", new OneBotSegmentDataDto { FileId = file.Source, Name = file.FileName }),
            LocationSegment location => Create("location", new OneBotSegmentDataDto
            {
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                Title = location.Title,
            }),
            ReplySegment reply => Create("reply", new OneBotSegmentDataDto { MessageId = reply.MessageId }),

            // 含 FaceSegment（OneBot 12 无表情段）与插件自定义段：降级为文本。
            _ => Create("text", new OneBotSegmentDataDto { Text = segment.ToPlainText() }),
        };
    }

    private static OneBotSegmentDto Create(string type, OneBotSegmentDataDto data)
        => new() { Type = type, Data = data };
}
