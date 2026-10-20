using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.Email;

/// <summary>
/// 邮件适配器：把自有 IMAP/SMTP 邮箱当作一个聊天平台。
/// </summary>
/// <remarks>
/// <para>
/// 骨架与 <c>ConsoleAdapter</c> / <c>OneBot12Adapter</c> 一致：<see cref="StartAsync"/>
/// 起后台收件循环后立即返回，<see cref="SendAsync"/> 由框架按平台名路由。
/// 区别在于「读标准输入 / 读 WebSocket 报文」换成了「IMAP 收件」，发送走 SMTP。
/// </para>
/// <para>
/// 入站只处理未读邮件（<c>SearchQuery.NotSeen</c>），处理完标记为已读：
/// 重启后不会重复发布，处理失败的邮件也留有重试机会。
/// </para>
/// <para>
/// 出站每次发送新建一条 SMTP 连接：邮件量下连接开销可以忽略，
/// 换来的是不需要维护连接池，也不存在 MailKit 客户端不可跨线程共享的问题。
/// </para>
/// <para>
/// 线程模型：收件循环跑在单个后台任务上（MailKit 的 IMAP 客户端不是线程安全的），
/// <see cref="SendAsync"/> 使用独立的 SMTP 客户端，二者互不共享状态。
/// </para>
/// </remarks>
public sealed class EmailAdapter : IAdapter
{
    /// <summary>平台名；必须与事件中 <see cref="ConversationId.Platform"/> 一致，发送时据此路由。</summary>
    public const string PlatformName = "email";

    /// <summary>关闭时等待收件循环收尾的上限。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly IEventPublisher _publisher;
    private readonly EmailAdapterOptions _options;
    private readonly EmailSenderPolicy _senderPolicy;
    private readonly ILogger<EmailAdapter> _logger;

    /// <summary>收件循环的生存期令牌。刻意不复用宿主传入的取消令牌——那是启动期的，不代表运行期。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>机器人发件地址（已剥离可选的显示名）；发送时写入 From。未配置时为空串。</summary>
    private readonly string _fromAddress;

    /// <summary>机器人自己的地址（已规范化）；用于过滤自发邮件。未配置时为空串。</summary>
    private readonly string _selfAddress;

    /// <summary>收件循环任务；非 null 即表示已启动（用作幂等标志）。</summary>
    private Task? _receiveLoop;

    /// <summary>构造函数注入。</summary>
    /// <param name="publisher">事件入口；适配器把邮件转换成 Core 事件后由此送入管道。</param>
    /// <param name="options">适配器配置。</param>
    /// <param name="logger">结构化日志。</param>
    public EmailAdapter(
        IEventPublisher publisher,
        IOptions<EmailAdapterOptions> options,
        ILogger<EmailAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _publisher = publisher;
        _options = options.Value;
        _logger = logger;

        _fromAddress = ResolveFromAddress(_options.FromAddress);
        _selfAddress = string.IsNullOrWhiteSpace(_fromAddress)
            ? string.Empty
            : EmailMessageTranslator.NormalizeAddress(_fromAddress);

        _senderPolicy = new EmailSenderPolicy(_options.AllowedSenders, _options.AllowedDomains);
    }

    /// <inheritdoc />
    public string Name => PlatformName;

    /// <inheritdoc />
    public string Platform => PlatformName;

    /// <summary>
    /// 启动收件循环。立即返回，不阻塞宿主启动；重复调用是幂等的。
    /// </summary>
    /// <remarks>
    /// 未启用时什么都不做，宿主可以在不配置邮箱的情况下共用同一份 appsettings.json。
    /// 启用后配置非法（缺主机、缺凭据、白名单为空等）直接抛出，由宿主 fail fast，
    /// 避免适配器「看起来启动了」却永远收不到邮件。
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_receiveLoop is not null)
        {
            return Task.CompletedTask;
        }

        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "邮件适配器未启用（{Section}:Enabled = false）。",
                EmailAdapterOptions.SectionName);

            return Task.CompletedTask;
        }

        ValidateOptions();

        _logger.LogInformation(
            "邮件适配器已启动，监听邮箱 {Mailbox}@{ImapHost}。",
            _options.Mailbox,
            _options.ImapHost);

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>停止收件循环并断开 IMAP 连接。重复调用是幂等的。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_receiveLoop is null)
        {
            return;
        }

        try
        {
            await _receiveLoop.WaitAsync(ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("邮件收件循环未在超时内结束，交由进程退出回收。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主不再等待：关闭流程继续，收件循环由进程退出回收。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "邮件收件循环结束时抛出未预期的异常。");
        }

        _logger.LogInformation("邮件适配器已停止。");
    }

    /// <summary>
    /// 发送邮件。框架按 <see cref="ConversationId.Platform"/> 路由到这里。
    /// </summary>
    /// <remarks>按 <see cref="IAdapter"/> 契约：失败以 <see cref="SendResult"/> 返回，不抛异常。</remarks>
    public async ValueTask<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_options.Enabled)
        {
            return SendResult.Fail(SendFailureKind.NotConnected, "邮件适配器未启用，消息未发送。");
        }

        // 首版只支持「发件人即收件人」的私聊形态。群组/邮件列表语义留待后续版本，
        // 与其猜一个收件人，不如明确失败。
        if (message.Conversation.Type != ConversationType.Private)
        {
            return SendResult.Fail(
                SendFailureKind.Rejected,
                "邮件适配器首版只支持私聊会话（会话 id 为收件人地址）。");
        }

        MimeMessage mimeMessage;
        try
        {
            mimeMessage = EmailOutboundTranslator.Create(
                message,
                _fromAddress,
                _options.FromDisplayName,
                _options.Subject,
                DateTimeOffset.Now);
        }
        catch (ParseException ex)
        {
            _logger.LogWarning(ex, "无法解析收件人地址 {Address}。", message.Conversation.Id);
            return SendResult.Fail(SendFailureKind.Rejected, $"收件人地址无效：{message.Conversation.Id}");
        }

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(
                _options.SmtpHost,
                _options.SmtpPort,
                ToSecureSocketOptions(_options.SmtpSecurity),
                cancellationToken).ConfigureAwait(false);

            await client.AuthenticateAsync(_options.UserName, _options.Password, cancellationToken)
                .ConfigureAwait(false);

            // MessageId 由 EmailOutboundTranslator 生成；属性本身可空，按契约原样回传。
            string? messageId = mimeMessage.MessageId;
            await client.SendAsync(mimeMessage, cancellationToken).ConfigureAwait(false);

            // 不显式 QUIT：using 的 Dispose 会在连接仍打开时尽力断开。
            // 发送成功后若再因断开失败而返回失败，会诱导调用方重发，造成重复邮件。
            return SendResult.Ok(messageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：正常控制流，向上传播。
            throw;
        }
        catch (SmtpCommandException ex)
        {
            // 服务器明确拒绝（认证失败、收件人不存在、被反垃圾策略拦截等）。
            _logger.LogError(ex, "SMTP 服务器拒绝向 {Conversation} 发送邮件。", message.Conversation);
            return SendResult.Fail(SendFailureKind.Rejected, ex.Message);
        }
        catch (Exception ex)
        {
            // IAdapter 契约：平台异常转失败结果，不穿透到插件调用点。
            _logger.LogError(ex, "向 {Conversation} 发送邮件时失败。", message.Conversation);
            return SendResult.Fail(SendFailureKind.Unknown, ex.Message);
        }
    }

    /// <summary>收件外层循环：连接断开后按配置间隔重连，直到适配器停止。</summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunReceiveSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "邮件接收连接中断，{Delay} 后重连。", _options.ReconnectDelay);
            }

            try
            {
                await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 一次 IMAP 会话：连接、认证、打开文件夹，然后「检查未读 → IDLE」循环。
    /// </summary>
    private async Task RunReceiveSessionAsync(CancellationToken cancellationToken)
    {
        using var client = new ImapClient();

        await client.ConnectAsync(
            _options.ImapHost,
            _options.ImapPort,
            ToSecureSocketOptions(_options.ImapSecurity),
            cancellationToken).ConfigureAwait(false);

        await client.AuthenticateAsync(_options.UserName, _options.Password, cancellationToken)
            .ConfigureAwait(false);

        IMailFolder folder = await OpenMailboxAsync(client, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "已连接 IMAP 服务器 {Host}:{Port}，开始监听 {Mailbox}。",
            _options.ImapHost,
            _options.ImapPort,
            _options.Mailbox);

        // 少数服务器不支持 IDLE：只要遇到一次，本次会话就退化为定时轮询。
        var idleSupported = true;

        while (client.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            await HandleUnseenMessagesAsync(folder, cancellationToken).ConfigureAwait(false);

            if (!idleSupported)
            {
                await Task.Delay(_options.IdleTimeout, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleTimeout.CancelAfter(_options.IdleTimeout);

            try
            {
                // MailKit 的 IDLE 在收到新邮件通知时不会自行返回；由 idleTimeout 结束本次 IDLE，
                // 回到循环顶部重新检查未读邮件。服务器要求约 29 分钟内重发 IDLE，默认 30 秒远低于该上限。
                await client.IdleAsync(idleTimeout.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // IDLE 到期：正常控制流。
            }
            catch (NotSupportedException)
            {
                idleSupported = false;
                _logger.LogWarning(
                    "IMAP 服务器不支持 IDLE，改用轮询（间隔 {Interval}）。",
                    _options.IdleTimeout);
            }
        }
    }

    /// <summary>按配置打开监听的文件夹。</summary>
    private async Task<IMailFolder> OpenMailboxAsync(ImapClient client, CancellationToken cancellationToken)
    {
        IMailFolder folder = string.Equals(_options.Mailbox, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(_options.Mailbox, cancellationToken).ConfigureAwait(false);

        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>处理当前所有未读邮件；每封邮件处理完（含被过滤）都标记为已读。</summary>
    private async Task HandleUnseenMessagesAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        IList<UniqueId> uids = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken)
            .ConfigureAwait(false);

        if (uids.Count == 0)
        {
            return;
        }

        foreach (UniqueId uid in uids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            MimeMessage message;
            try
            {
                message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 读取失败通常是网络问题：不标记已读，下一轮重试。
                _logger.LogError(ex, "读取邮件 UID {Uid} 失败，跳过本轮。", uid);
                continue;
            }

            try
            {
                await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 毒消息：持续重试只会阻塞后续邮件，记录后照常标记已读。
                _logger.LogError(ex, "处理邮件 UID {Uid} 失败，已跳过。", uid);
            }

            await MarkSeenAsync(folder, uid, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>把一封邮件翻译、过滤后送入事件管道。</summary>
    private async ValueTask DispatchAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        MessageEvent? coreEvent = EmailMessageTranslator.Translate(
            message,
            Platform,
            string.IsNullOrWhiteSpace(_selfAddress) ? null : _selfAddress);

        if (coreEvent is null)
        {
            _logger.LogDebug(
                "跳过邮件 {MessageId}：无发件人、自动生成/邮件列表流量，或发件人是机器人自己。",
                message.MessageId);

            return;
        }

        if (!_senderPolicy.IsAllowed(coreEvent.SenderId))
        {
            _logger.LogWarning(
                "已拒绝发件人 {Sender} 的邮件：不在 AllowedSenders / AllowedDomains 中。",
                coreEvent.SenderId);

            return;
        }

        await _publisher.PublishAsync(coreEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>标记邮件为已读；失败只记录日志，下一轮可能重复处理一次。</summary>
    private async Task MarkSeenAsync(IMailFolder folder, UniqueId uid, CancellationToken cancellationToken)
    {
        try
        {
            await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "标记邮件 UID {Uid} 为已读失败，它可能被重复处理。", uid);
        }
    }

    /// <summary>
    /// 启用状态下校验必填项。缺少白名单时直接拒绝启动：
    /// 邮件地址无法像平台用户 id 那样可信，默认拒绝是唯一安全的默认值。
    /// </summary>
    private void ValidateOptions()
    {
        Require(!string.IsNullOrWhiteSpace(_options.ImapHost), "ImapHost 不能为空");
        Require(_options.ImapPort is > 0 and <= 65535, "ImapPort 必须在 1-65535 之间");
        Require(!string.IsNullOrWhiteSpace(_options.Mailbox), "Mailbox 不能为空");
        Require(_options.IdleTimeout > TimeSpan.Zero, "IdleTimeout 必须大于 0");
        Require(_options.ReconnectDelay > TimeSpan.Zero, "ReconnectDelay 必须大于 0");

        Require(!string.IsNullOrWhiteSpace(_options.SmtpHost), "SmtpHost 不能为空");
        Require(_options.SmtpPort is > 0 and <= 65535, "SmtpPort 必须在 1-65535 之间");

        Require(!string.IsNullOrWhiteSpace(_options.UserName), "UserName 不能为空");
        Require(!string.IsNullOrWhiteSpace(_options.Password), "Password 不能为空");
        Require(!string.IsNullOrWhiteSpace(_options.FromAddress), "FromAddress 不能为空");
        Require(MailboxAddress.TryParse(_options.FromAddress, out _), "FromAddress 必须是合法的邮箱地址");
        Require(!string.IsNullOrWhiteSpace(_options.Subject), "Subject 不能为空");

        Require(
            _senderPolicy.HasRules,
            "AllowedSenders 与 AllowedDomains 不能同时为空（适配器默认拒绝所有发件人）");
    }

    /// <summary>校验单项配置；不满足即抛出，由宿主启动流程报告。</summary>
    private static void Require(bool condition, string requirement)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"邮件适配器配置无效：{requirement}。");
        }
    }

    /// <summary>
    /// 从配置值中取出纯邮箱地址。
    /// </summary>
    /// <remarks>
    /// 配置允许写成 <c>PulseBot &lt;bot@example.com&gt;</c> 这种带显示名的形式，
    /// 但构造 MailboxAddress 时必须只传地址部分，否则地址会被当作非法字符串。
    /// </remarks>
    private static string ResolveFromAddress(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return string.Empty;
        }

        return MailboxAddress.TryParse(configured, out MailboxAddress? mailbox)
            ? mailbox.Address
            : configured.Trim();
    }

    /// <summary>把适配器自己的加密枚举映射为 MailKit 的选项。</summary>
    private static SecureSocketOptions ToSecureSocketOptions(EmailSocketSecurity security) => security switch
    {
        EmailSocketSecurity.None => SecureSocketOptions.None,
        EmailSocketSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        EmailSocketSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto,
    };
}
