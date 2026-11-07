namespace AnBot.Admin.Web.Services;

/// <summary>
/// 后台前端使用的命名 HttpClient。
/// </summary>
/// <remarks>
/// 两个客户端的区别只在消息处理器链：
/// <see cref="Auth"/> 不带认证处理器（登录/刷新本身就不需要 Bearer，
/// 且刷新失败时不能再触发刷新，否则会递归）；
/// <see cref="Admin"/> 带 Bearer 注入与 401 自动刷新。
/// </remarks>
public static class HttpClientNames
{
    /// <summary>认证请求（登录、刷新、登出）。</summary>
    public const string Auth = "anbot-auth";

    /// <summary>业务请求（监控、账号、插件、名单、审计）。</summary>
    public const string Admin = "anbot-admin";
}
