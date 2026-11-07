using MimeKit;
using MimeKit.Utils;
using AnBot.Core.Adapters;

namespace AnBot.Adapter.Email;

/// <summary>
/// 把 Core 的出站消息转换为 MIME 邮件。
/// </summary>
/// <remarks>
/// 纯函数，便于在不连接 SMTP 的情况下断言收件人、主题、回复头与正文。
/// 首版只发送纯文本：消息中的图片/文件等段由 <c>Message.ToString()</c> 降级为占位符，
/// 与「纯文本最小闭环」的实现范围一致。
/// </remarks>
public static class EmailOutboundTranslator
{
    /// <summary>构造待发送的邮件。</summary>
    /// <param name="outbound">Core 的出站消息；会话 id 必须是可解析的邮箱地址。</param>
    /// <param name="fromAddress">机器人发件地址。</param>
    /// <param name="fromDisplayName">发件人展示名，可为 null。</param>
    /// <param name="subject">主动发送时使用的主题。</param>
    /// <param name="date">邮件时间；由调用方传入便于测试。</param>
    /// <exception cref="MimeKit.ParseException">收件人地址无法解析。</exception>
    public static MimeMessage Create(
        OutboundMessage outbound,
        string fromAddress,
        string? fromDisplayName,
        string subject,
        DateTimeOffset date)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAddress);
        ArgumentNullException.ThrowIfNull(subject);

        var message = new MimeMessage
        {
            Subject = BuildSubject(subject, outbound.IsReply),
            Date = date,
            MessageId = MimeUtils.GenerateMessageId(),
            Body = new TextPart("plain") { Text = outbound.Message.ToString() },
        };

        message.From.Add(new MailboxAddress(fromDisplayName ?? string.Empty, fromAddress));
        message.To.Add(MailboxAddress.Parse(outbound.Conversation.Id));

        // RFC 3834：声明本邮件由程序自动生成/回复。收信方的自动应答器据此不会再回一封，
        // 与本适配器入站的 Auto-Submitted 过滤形成双向的防循环保护。
        message.Headers.Add(
            "Auto-Submitted",
            outbound.IsReply ? "auto-replied" : "auto-generated");

        if (!string.IsNullOrWhiteSpace(outbound.ReplyToMessageId))
        {
            string replyTo = NormalizeMessageId(outbound.ReplyToMessageId);

            // 平台原生回复在邮件里的等价物：In-Reply-To 指向被回复邮件，
            // References 保留整条线程链（首版只有被回复的这一条）。
            message.InReplyTo = replyTo;
            message.References.Add(replyTo);
        }

        return message;
    }

    /// <summary>回复时加 <c>Re: </c> 前缀；已经带前缀则不重复添加。</summary>
    private static string BuildSubject(string subject, bool isReply)
    {
        string trimmed = subject.Trim();
        return isReply && !trimmed.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? $"Re: {trimmed}"
            : trimmed;
    }

    /// <summary>
    /// 规范化被回复邮件的 Message-ID，并去掉控制字符。
    /// </summary>
    /// <remarks>
    /// 该值来自入站邮件头（不受信任），直接写入 In-Reply-To 会带来邮件头注入风险；
    /// 剔除 CR/LF 等控制字符后再使用。
    /// </remarks>
    private static string NormalizeMessageId(string messageId)
    {
        string cleaned = string.Concat(messageId.Trim().Where(static c => !char.IsControl(c)));
        if (cleaned.Length >= 2 && cleaned[0] == '<' && cleaned[^1] == '>')
        {
            return cleaned;
        }

        return $"<{cleaned}>";
    }
}
