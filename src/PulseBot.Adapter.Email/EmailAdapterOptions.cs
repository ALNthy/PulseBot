namespace PulseBot.Adapter.Email;

/// <summary>
/// 邮件适配器配置。
/// </summary>
/// <remarks>
/// <para>
/// 配置节为 <c>Adapters:Email</c>。适配器默认关闭：只有显式把 <see cref="Enabled"/>
/// 设为 <c>true</c> 并填写完整的收发参数才会连接邮箱，避免默认宿主因缺少邮箱配置而启动失败。
/// </para>
/// <para>
/// <see cref="Password"/> 属于敏感配置，生产环境应通过环境变量注入
/// （例如 <c>Adapters__Email__Password</c>），不要直接写进 appsettings.json。
/// </para>
/// </remarks>
public sealed class EmailAdapterOptions
{
    /// <summary>配置节名（appsettings.json 中为 <c>"Adapters:Email"</c>）。</summary>
    public const string SectionName = "Adapters:Email";

    /// <summary>是否启用邮件适配器。</summary>
    public bool Enabled { get; set; }

    /// <summary>IMAP 服务器主机名。</summary>
    public string ImapHost { get; set; } = string.Empty;

    /// <summary>IMAP 端口；SSL 直连通常为 993，STARTTLS 通常为 143。</summary>
    public int ImapPort { get; set; } = 993;

    /// <summary>IMAP 连接加密方式。</summary>
    public EmailSocketSecurity ImapSecurity { get; set; } = EmailSocketSecurity.SslOnConnect;

    /// <summary>
    /// 监听的邮件文件夹，默认 <c>INBOX</c>。
    /// </summary>
    /// <remarks>子文件夹需使用邮件服务器自己的分隔符（多数为 <c>/</c>，部分为 <c>.</c>）。</remarks>
    public string Mailbox { get; set; } = "INBOX";

    /// <summary>
    /// 单次 IMAP IDLE 的时长：到期后重新检查未读邮件。
    /// </summary>
    /// <remarks>
    /// MailKit 的 IDLE 任务在收到「新邮件」通知时不会自行返回，因此本适配器以该值
    /// 作为检查周期：设得越小，命令响应越及时，但 IMAP 往返也越频繁。
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>连接断开后的重连等待时间。</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>SMTP 服务器主机名。</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>SMTP 端口；SSL 直连通常为 465，STARTTLS 通常为 587。</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>SMTP 连接加密方式。</summary>
    public EmailSocketSecurity SmtpSecurity { get; set; } = EmailSocketSecurity.StartTls;

    /// <summary>IMAP 与 SMTP 共用的登录用户名（通常是完整邮箱地址）。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>IMAP 与 SMTP 共用的登录密码或授权码。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>机器人发件地址；同时用于过滤「自己发给自己」造成的自动回复循环。</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>发件人展示名。</summary>
    public string FromDisplayName { get; set; } = "PulseBot";

    /// <summary>主动发送（非回复）时使用的邮件主题；回复时自动加 <c>Re: </c> 前缀。</summary>
    public string Subject { get; set; } = "PulseBot";

    /// <summary>
    /// 允许与机器人交互的发件人地址（完整地址，不区分大小写）。
    /// </summary>
    /// <remarks>
    /// 邮件头的 <c>From</c> 可以伪造，因此不能像即时通讯平台那样把用户 id 当作可信身份。
    /// 只处理白名单内的邮件是适配器的默认安全边界；可信的 SPF/DKIM/DMARC 校验由收信服务商完成，
    /// 本适配器不重复实现。
    /// </remarks>
    public string[] AllowedSenders { get; set; } = [];

    /// <summary>
    /// 允许与机器人交互的发件人域名（不含 <c>@</c>，不区分大小写），与地址白名单取并集。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AllowedSenders"/> 同时为空时适配器拒绝启动——默认拒绝所有发件人，
    /// 避免误配置后一个公开邮箱直接变成命令入口。
    /// </remarks>
    public string[] AllowedDomains { get; set; } = [];
}
