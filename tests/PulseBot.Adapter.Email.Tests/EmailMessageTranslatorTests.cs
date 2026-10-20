using MimeKit;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.Email.Tests;

/// <summary>
/// 入站翻译：邮件头/正文 → Core 消息事件，以及自动流量与自发邮件的过滤。
/// </summary>
[Trait("Category", "Unit")]
public sealed class EmailMessageTranslatorTests
{
    private const string Platform = "email";
    private const string BotAddress = "bot@example.com";

    [Fact]
    public void Translate_MapsHeadersAndBody()
    {
        MimeMessage message = CreateTextMessage(
            fromAddress: "Alice@Example.COM",
            fromName: "Alice",
            text: "你好");

        MessageEvent result = Assert.IsType<MessageEvent>(
            EmailMessageTranslator.Translate(message, Platform, BotAddress));

        Assert.Equal(Platform, result.Conversation.Platform);
        Assert.Equal(ConversationType.Private, result.Conversation.Type);
        Assert.Equal("alice@example.com", result.Conversation.Id);
        Assert.Equal("alice@example.com", result.SenderId);
        Assert.Equal("Alice", result.SenderDisplayName);
        Assert.Equal("<mail-1@example.com>", result.MessageId);
        Assert.Equal("你好", result.PlainText);
    }

    [Fact]
    public void Translate_PrefersPlainTextOverHtml()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.MessageId = "<mail-2@example.com>";
        message.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "纯文本版本" },
            new TextPart("html") { Text = "<p>HTML 版本</p>" },
        };

        MessageEvent result = Assert.IsType<MessageEvent>(
            EmailMessageTranslator.Translate(message, Platform, BotAddress));

        Assert.Equal("纯文本版本", result.PlainText);
    }

    [Fact]
    public void Translate_ConvertsHtmlOnlyBodyToPlainText()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.MessageId = "<mail-3@example.com>";
        message.Body = new TextPart("html")
        {
            Text = "<html><body><!-- 注释里的 <p>命令</p> 不应出现 --><p>你好&amp;世界</p>"
                + "<p>第二行</p><script>alert('x')</script></body></html>",
        };

        MessageEvent result = Assert.IsType<MessageEvent>(
            EmailMessageTranslator.Translate(message, Platform, BotAddress));

        Assert.Contains("你好&世界", result.PlainText);
        Assert.Contains("第二行", result.PlainText);
        Assert.DoesNotContain("<p>", result.PlainText);
        Assert.DoesNotContain("alert", result.PlainText);
        Assert.DoesNotContain("命令", result.PlainText);

        // 命令分发会对正文做 Trim，这里只确保没有残留标签。
        Assert.StartsWith("你好", result.PlainText);
    }

    [Fact]
    public void Translate_AlwaysProvidesMessageId()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.Body = new TextPart("plain") { Text = "无显式 Message-ID" };

        MessageEvent result = Assert.IsType<MessageEvent>(
            EmailMessageTranslator.Translate(message, Platform, BotAddress));

        // MimeKit 在访问 MessageId 时会自动生成一个（这正是我们期望的兜底行为）。
        Assert.StartsWith("<", result.MessageId);
        Assert.EndsWith(">", result.MessageId);
    }

    [Fact]
    public void NormalizeMessageId_GeneratesPlaceholderWhenMissing()
    {
        string normalized = EmailMessageTranslator.NormalizeMessageId(null);

        Assert.StartsWith("<pulsebot-", normalized);
        Assert.EndsWith(">", normalized);
        Assert.DoesNotContain(" ", normalized);
    }

    [Fact]
    public void Translate_ReturnsNullWhenSenderMissing()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("plain") { Text = "没有发件人" },
        };

        Assert.Null(EmailMessageTranslator.Translate(message, Platform, BotAddress));
    }

    [Fact]
    public void Translate_ReturnsNullForSelfSentMail()
    {
        MimeMessage message = CreateTextMessage(
            fromAddress: "BOT@example.com",
            fromName: "PulseBot",
            text: "自己发给自己");

        Assert.Null(EmailMessageTranslator.Translate(message, Platform, BotAddress));
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied")]
    [InlineData("Auto-Submitted", "auto-generated")]
    [InlineData("Precedence", "bulk")]
    [InlineData("Precedence", "list")]
    [InlineData("Precedence", "junk")]
    [InlineData("List-Id", "<list.example.com>")]
    public void Translate_ReturnsNullForAutomatedMail(string headerName, string headerValue)
    {
        MimeMessage message = CreateTextMessage(
            fromAddress: "alice@example.com",
            fromName: "Alice",
            text: "自动流量");
        message.Headers.Add(headerName, headerValue);

        Assert.Null(EmailMessageTranslator.Translate(message, Platform, BotAddress));
    }

    [Fact]
    public void Translate_AllowsAutoSubmittedNo()
    {
        MimeMessage message = CreateTextMessage(
            fromAddress: "alice@example.com",
            fromName: "Alice",
            text: "人工邮件");
        message.Headers.Add("Auto-Submitted", "no");

        Assert.NotNull(EmailMessageTranslator.Translate(message, Platform, BotAddress));
    }

    [Fact]
    public void NormalizeMessageId_StripsControlCharactersAndAddsBrackets()
    {
        string normalized = EmailMessageTranslator.NormalizeMessageId(" abc@example.com\r\nBcc: victim@example.com ");

        Assert.StartsWith("<", normalized);
        Assert.EndsWith(">", normalized);
        Assert.DoesNotContain("\r", normalized);
        Assert.DoesNotContain("\n", normalized);
    }

    /// <summary>构造一封最小可翻译邮件。</summary>
    private static MimeMessage CreateTextMessage(string fromAddress, string fromName, string text)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(new MailboxAddress("PulseBot", BotAddress));
        message.Subject = "测试";
        message.MessageId = "<mail-1@example.com>";
        message.Body = new TextPart("plain") { Text = text };
        return message;
    }
}
