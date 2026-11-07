using MimeKit;
using AnBot.Core.Adapters;
using AnBot.Core.Messages;

namespace AnBot.Adapter.Email.Tests;

/// <summary>
/// 出站翻译：Core 出站消息 → MIME 邮件，不连接 SMTP。
/// </summary>
[Trait("Category", "Unit")]
public sealed class EmailOutboundTranslatorTests
{
    private static readonly DateTimeOffset FixedDate = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_MapsAddressesSubjectAndBody()
    {
        var outbound = new OutboundMessage
        {
            Conversation = new ConversationId("email", ConversationType.Private, "bob@example.com"),
            Message = Message.Text("你好"),
        };

        MimeMessage message = EmailOutboundTranslator.Create(
            outbound, "bot@example.com", "AnBot", "AnBot", FixedDate);

        Assert.Equal("bob@example.com", Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("bot@example.com", Assert.Single(message.From.Mailboxes).Address);
        Assert.Equal("AnBot", Assert.Single(message.From.Mailboxes).Name);
        Assert.Equal("AnBot", message.Subject);
        Assert.Equal("你好", message.TextBody);
        Assert.Equal(FixedDate, message.Date);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        Assert.Equal("auto-generated", message.Headers["Auto-Submitted"]);
    }

    [Fact]
    public void Create_SetsReplyHeadersAndSubjectPrefix()
    {
        var outbound = new OutboundMessage
        {
            Conversation = new ConversationId("email", ConversationType.Private, "bob@example.com"),
            Message = Message.Text("收到"),
            ReplyToMessageId = "<original@example.com>",
        };

        MimeMessage message = EmailOutboundTranslator.Create(
            outbound, "bot@example.com", "AnBot", "AnBot", FixedDate);

        // MailKit 在属性读取时会去掉 Message-ID 的尖括号（写回报文时再补上），
        // 因此这里断言的是不含尖括号的形式。
        Assert.Equal("Re: AnBot", message.Subject);
        Assert.Equal("original@example.com", message.InReplyTo);
        Assert.Contains("original@example.com", message.References);
        Assert.Equal("auto-replied", message.Headers["Auto-Submitted"]);
    }

    [Fact]
    public void Create_DoesNotDoubleReplyPrefix()
    {
        var outbound = new OutboundMessage
        {
            Conversation = new ConversationId("email", ConversationType.Private, "bob@example.com"),
            Message = Message.Text("收到"),
            ReplyToMessageId = "<original@example.com>",
        };

        MimeMessage message = EmailOutboundTranslator.Create(
            outbound, "bot@example.com", "AnBot", "Re: 已有前缀", FixedDate);

        Assert.Equal("Re: 已有前缀", message.Subject);
    }

    [Fact]
    public void Create_StripsControlCharactersFromReplyMessageId()
    {
        var outbound = new OutboundMessage
        {
            Conversation = new ConversationId("email", ConversationType.Private, "bob@example.com"),
            Message = Message.Text("收到"),
            ReplyToMessageId = "<original@example.com>\r\nBcc: victim@example.com",
        };

        MimeMessage message = EmailOutboundTranslator.Create(
            outbound, "bot@example.com", "AnBot", "AnBot", FixedDate);

        string inReplyTo = Assert.IsType<string>(message.InReplyTo);
        Assert.DoesNotContain("\r", inReplyTo);
        Assert.DoesNotContain("\n", inReplyTo);
    }

    [Fact]
    public void Create_RendersNonTextSegmentsAsPlaceholders()
    {
        var outbound = new OutboundMessage
        {
            Conversation = new ConversationId("email", ConversationType.Private, "bob@example.com"),
            Message = new Message(new TextSegment("看图："), new ImageSegment("https://example.com/a.png")),
        };

        MimeMessage message = EmailOutboundTranslator.Create(
            outbound, "bot@example.com", "AnBot", "AnBot", FixedDate);

        Assert.Equal("看图：[图片]", message.TextBody);
    }
}
