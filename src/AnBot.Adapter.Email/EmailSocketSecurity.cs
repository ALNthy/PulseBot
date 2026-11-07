namespace AnBot.Adapter.Email;

/// <summary>
/// 邮件连接加密方式，在适配器内部映射到 MailKit 的 <c>SecureSocketOptions</c>。
/// </summary>
/// <remarks>
/// 不使用 MailKit 的枚举直接作为配置类型：配置项属于适配器的公开契约，
/// 不应把第三方库的类型泄漏给宿主与配置文件。
/// </remarks>
public enum EmailSocketSecurity
{
    /// <summary>由 MailKit 根据端口自动判断（143/587 用 STARTTLS，993/465 用 SSL 直连）。</summary>
    Auto = 0,

    /// <summary>不加密，仅适用于本机或受信任内网。</summary>
    None = 1,

    /// <summary>建立连接即使用 TLS，通常配合 993（IMAP）或 465（SMTP）端口。</summary>
    SslOnConnect = 2,

    /// <summary>先明文连接，再通过 STARTTLS 升级，通常配合 143（IMAP）或 587（SMTP）端口。</summary>
    StartTls = 3,
}
