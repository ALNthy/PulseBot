using MimeKit;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.Email;

/// <summary>
/// 把 MIME 邮件翻译为 Core 的消息事件。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：不访问网络、不读配置，便于单元测试。过滤规则也放在这里，因为它们判定的是
/// 邮件头本身（自动回复、邮件列表），与「谁允许使用机器人」的准入策略是两件事：
/// 前者是防止协议层面的自动互发，后者是安全边界（见 <see cref="EmailSenderPolicy"/>）。
/// </para>
/// <para>
/// 会话模型：一封邮件对应一个「私聊」会话，会话 id 就是发件地址；
/// 回复时发往同一地址即可。邮件列表、抄送等群组语义不在首版范围内。
/// </para>
/// </remarks>
public static class EmailMessageTranslator
{
    /// <summary>
    /// 翻译一封邮件；返回 <c>null</c> 表示不应发布为事件
    /// （无发件人、自动生成/自动回复、邮件列表流量，或发件人就是机器人自己）。
    /// </summary>
    /// <param name="message">已解析的邮件。</param>
    /// <param name="platform">写入会话标识的平台名，须与 <c>IAdapter.Platform</c> 一致。</param>
    /// <param name="selfAddress">机器人自己的地址；非空时忽略自发邮件，避免自动回复循环。</param>
    public static MessageEvent? Translate(MimeMessage message, string platform, string? selfAddress)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        MailboxAddress? sender = message.From.Mailboxes.FirstOrDefault();
        if (sender is null || string.IsNullOrWhiteSpace(sender.Address))
        {
            return null;
        }

        string senderAddress = NormalizeAddress(sender.Address);

        // 机器人收到自己发出的邮件（例如 SMTP 服务器把副本投回同一邮箱）：
        // 交给管道只会让机器人回复自己，直接丢弃。
        if (!string.IsNullOrWhiteSpace(selfAddress)
            && string.Equals(senderAddress, NormalizeAddress(selfAddress), StringComparison.Ordinal))
        {
            return null;
        }

        if (IsAutomated(message))
        {
            return null;
        }

        return new MessageEvent
        {
            Conversation = new ConversationId(platform, ConversationType.Private, senderAddress),
            Message = Message.Text(ExtractBody(message)),
            MessageId = NormalizeMessageId(message.MessageId),
            SenderId = senderAddress,
            SenderDisplayName = string.IsNullOrWhiteSpace(sender.Name) ? null : sender.Name,
        };
    }

    /// <summary>发件地址规范化：去空白并转小写，与权限/白名单配置的书写方式保持一致。</summary>
    /// <param name="address">原始地址。</param>
    public static string NormalizeAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Trim().ToLowerInvariant();
    }

    /// <summary>提取可读正文：优先 text/plain，只有 HTML 时降级为纯文本。</summary>
    /// <param name="message">已解析的邮件。</param>
    public static string ExtractBody(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string? textBody = message.TextBody;
        if (!string.IsNullOrWhiteSpace(textBody))
        {
            return textBody;
        }

        string? htmlBody = message.HtmlBody;
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            return HtmlTextConverter.Convert(htmlBody);
        }

        return string.Empty;
    }

    /// <summary>
    /// 是否属于自动生成 / 自动回复 / 邮件列表流量。
    /// </summary>
    /// <remarks>
    /// 这些邮件如果进入事件管道，很容易与对方的自动回复形成无限互发：
    /// 机器人回一封 → 对方假期应答 → 机器人再回……RFC 3834 的 <c>Auto-Submitted</c>、
    /// 传统的 <c>Precedence</c> 以及 <c>List-Id</c> 都是识别这类流量的标准信号。
    /// 首版一律丢弃，不做「对方是自动回复则只回一次」之类的状态跟踪。
    /// </remarks>
    /// <param name="message">已解析的邮件。</param>
    public static bool IsAutomated(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string? autoSubmitted = message.Headers["Auto-Submitted"];
        if (autoSubmitted is not null)
        {
            // RFC 3834：只有 "no" 表示人工发出的邮件，其余取值（auto-replied、auto-generated 等）
            // 都视为自动流量。
            string value = autoSubmitted.Trim();
            if (!value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string? precedence = message.Headers["Precedence"];
        if (precedence is not null)
        {
            string value = precedence.Trim();
            if (value.Equals("bulk", StringComparison.OrdinalIgnoreCase)
                || value.Equals("list", StringComparison.OrdinalIgnoreCase)
                || value.Equals("junk", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return message.Headers.Contains("List-Id");
    }

    /// <summary>
    /// 规范化 Message-ID：保证带尖括号；缺失时生成一个本地占位 id。
    /// </summary>
    /// <remarks>
    /// 部分畸形邮件没有 Message-ID，而事件模型要求该字段。生成占位 id 可以让消息
    /// 继续流向插件，但此时被动回复无法正确串起线程——这是可接受的降级。
    /// 控制字符一律剔除，避免来自不受信任邮件头的值被写回出站邮件头。
    /// </remarks>
    /// <param name="raw">邮件头中的原始 Message-ID。</param>
    public static string NormalizeMessageId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"<pulsebot-{Guid.NewGuid():N}@localhost>";
        }

        string id = string.Concat(raw.Trim().Where(static c => !char.IsControl(c)));
        if (id.Length >= 2 && id[0] == '<' && id[^1] == '>')
        {
            return id;
        }

        return $"<{id}>";
    }
}
