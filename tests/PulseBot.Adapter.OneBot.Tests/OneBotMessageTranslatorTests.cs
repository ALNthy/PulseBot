using PulseBot.Adapter.OneBot.V12.Protocol;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.OneBot.Tests;

/// <summary>Core 消息与 OneBot 12 消息段之间的双向映射。</summary>
[Trait("Category", "Unit")]
public sealed class OneBotMessageTranslatorTests
{
    /// <summary>入站：文本与提及类段映射到对应的 Core 段。</summary>
    [Fact]
    public void 入站文本与提及段映射到对应的Core段()
    {
        Message message = OneBotMessageTranslator.ToCoreMessage(
        [
            Segment("text", new OneBotSegmentDataDto { Text = "你好" }),
            Segment("mention", new OneBotSegmentDataDto { UserId = "10001" }),
            Segment("mention_all", new OneBotSegmentDataDto()),
            Segment("reply", new OneBotSegmentDataDto { MessageId = "m1" }),
        ]);

        Assert.Collection(
            message.Segments,
            s => Assert.Equal("你好", Assert.IsType<TextSegment>(s).Text),
            s => Assert.Equal("10001", Assert.IsType<AtSegment>(s).TargetId),
            s => Assert.IsType<MentionAllSegment>(s),
            s => Assert.Equal("m1", Assert.IsType<ReplySegment>(s).MessageId));
    }

    /// <summary>入站：媒体与位置段映射到对应的 Core 段；voice 与 audio 归为同一种音频段。</summary>
    [Fact]
    public void 入站媒体与位置段映射到对应的Core段()
    {
        Message message = OneBotMessageTranslator.ToCoreMessage(
        [
            Segment("image", new OneBotSegmentDataDto { FileId = "file:///a.png" }),
            Segment("voice", new OneBotSegmentDataDto { FileId = "file:///a.silk" }),
            Segment("audio", new OneBotSegmentDataDto { FileId = "file:///b.mp3" }),
            Segment("video", new OneBotSegmentDataDto { FileId = "file:///c.mp4" }),
            Segment("file", new OneBotSegmentDataDto { FileId = "file:///d.zip", Name = "d.zip" }),
            Segment("location", new OneBotSegmentDataDto { Latitude = 31.2, Longitude = 121.5, Title = "上海" }),
        ]);

        Assert.Collection(
            message.Segments,
            s => Assert.Equal("file:///a.png", Assert.IsType<ImageSegment>(s).Source),
            s => Assert.Equal("file:///a.silk", Assert.IsType<AudioSegment>(s).Source),
            s => Assert.Equal("file:///b.mp3", Assert.IsType<AudioSegment>(s).Source),
            s => Assert.Equal("file:///c.mp4", Assert.IsType<VideoSegment>(s).Source),
            s => Assert.Equal("d.zip", Assert.IsType<FileSegment>(s).FileName),
            s => Assert.Equal("上海", Assert.IsType<LocationSegment>(s).Title));
    }

    /// <summary>入站：实现端扩展的未知段降级为占位文本，而不是让整条消息丢失。</summary>
    [Fact]
    public void 入站未知段降级为占位文本()
    {
        Message message = OneBotMessageTranslator.ToCoreMessage(
            [Segment("markdown", new OneBotSegmentDataDto { Text = "# 标题" })]);

        var text = Assert.IsType<TextSegment>(Assert.Single(message.Segments));
        Assert.Equal("[不支持的消息段: markdown]", text.Text);
    }

    /// <summary>入站：段数组缺失或为空时得到空消息。</summary>
    [Fact]
    public void 入站空段数组得到空消息()
    {
        Assert.Empty(OneBotMessageTranslator.ToCoreMessage(null).Segments);
        Assert.Empty(OneBotMessageTranslator.ToCoreMessage([]).Segments);
    }

    /// <summary>出站：Core 段逐一映射回对应的 OneBot 段类型与参数。</summary>
    [Fact]
    public void 出站Core段映射到对应的OneBot段()
    {
        List<OneBotSegmentDto> segments = OneBotMessageTranslator.ToOneBotSegments(
            new Message(
                new TextSegment("你好"),
                new AtSegment("10001"),
                new MentionAllSegment(),
                new ImageSegment("file:///a.png"),
                new AudioSegment("file:///a.silk"),
                new VideoSegment("file:///c.mp4"),
                new FileSegment("file:///d.zip", "d.zip"),
                new LocationSegment(31.2, 121.5, "上海"),
                new ReplySegment("m1")));

        Assert.Collection(
            segments,
            s => AssertSegment(s, "text", d => Assert.Equal("你好", d.Text)),
            s => AssertSegment(s, "mention", d => Assert.Equal("10001", d.UserId)),
            s => AssertSegment(s, "mention_all", _ => { }),
            s => AssertSegment(s, "image", d => Assert.Equal("file:///a.png", d.FileId)),
            s => AssertSegment(s, "voice", d => Assert.Equal("file:///a.silk", d.FileId)),
            s => AssertSegment(s, "video", d => Assert.Equal("file:///c.mp4", d.FileId)),
            s => AssertSegment(s, "file", d => Assert.Equal("d.zip", d.Name)),
            s => AssertSegment(s, "location", d => Assert.Equal("上海", d.Title)),
            s => AssertSegment(s, "reply", d => Assert.Equal("m1", d.MessageId)));
    }

    /// <summary>出站：表情段在 OneBot 12 没有对应类型，降级为它的纯文本表示。</summary>
    [Fact]
    public void 出站表情段降级为文本()
    {
        var face = new FaceSegment("1");

        List<OneBotSegmentDto> segments = OneBotMessageTranslator.ToOneBotSegments(new Message(face));

        AssertSegment(Assert.Single(segments), "text", d => Assert.Equal(face.ToPlainText(), d.Text));
    }

    /// <summary>出站：插件自定义的段类型没有平台对应物，同样降级为纯文本。</summary>
    [Fact]
    public void 出站自定义段降级为文本()
    {
        var sticker = new StickerSegment("sticker-1");

        List<OneBotSegmentDto> segments = OneBotMessageTranslator.ToOneBotSegments(new Message(sticker));

        AssertSegment(Assert.Single(segments), "text", d => Assert.Equal(sticker.ToPlainText(), d.Text));
    }

    // ---------- 测试基础设施 ----------

    private static OneBotSegmentDto Segment(string type, OneBotSegmentDataDto data)
        => new() { Type = type, Data = data };

    /// <summary>断言段类型，并对段参数做进一步检查。</summary>
    private static void AssertSegment(OneBotSegmentDto segment, string expectedType, Action<OneBotSegmentDataDto> assertData)
    {
        Assert.Equal(expectedType, segment.Type);
        assertData(segment.Data);
    }

    /// <summary>模拟插件扩展的消息段：Core 允许派生，适配器无从得知它的平台映射。</summary>
    private sealed record StickerSegment(string Id) : MessageSegment
    {
        /// <inheritdoc />
        public override string ToPlainText() => "(贴纸)";
    }
}
