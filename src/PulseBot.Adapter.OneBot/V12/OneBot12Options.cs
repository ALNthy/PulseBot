namespace PulseBot.Adapter.OneBot.V12;

/// <summary>OneBot 12 适配器配置。</summary>
public sealed class OneBot12Options
{
    /// <summary>配置节名（appsettings.json 中的路径）。</summary>
    public const string SectionName = "Adapters:OneBot12";

    /// <summary>
    /// 监听主机名。默认只监听本机回环地址——<see cref="System.Net.HttpListener"/> 在 Windows 上
    /// 监听 <c>localhost</c> 以外的前缀需要管理员权限或预先注册 URL ACL，
    /// 跨机部署时改为 <c>+</c> 或具体 IP 并自行完成该注册。
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>监听端口。OneBot 实现端配置的反向连接地址须与此一致。</summary>
    public int Port { get; set; } = 6700;

    /// <summary>反向 WebSocket 路径。</summary>
    public string Path { get; set; } = "/onebot/v12/";

    /// <summary>
    /// 访问令牌。非空时要求实现端在握手请求中携带 <c>Authorization: Bearer &lt;token&gt;</c>，
    /// 不匹配直接拒绝连接（返回 401）；为空则不校验，仅适用于本机回环场景。
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>等待实现端返回动作响应的超时；超过即认为本次发送失败。</summary>
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
