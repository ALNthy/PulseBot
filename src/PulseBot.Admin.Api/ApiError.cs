namespace PulseBot.Admin.Api;

/// <summary>
/// 统一的错误响应体。
/// </summary>
/// <remarks>
/// 后台接口只返回这一种错误形状，调用方不需要按端点分别处理。
/// <see cref="Message"/> 面向人类阅读，**不得**携带堆栈或内部类型名——
/// 详细原因留在服务端的结构化日志与审计里。
/// </remarks>
/// <param name="Code">机器可读的错误码，见 <see cref="ApiErrorCodes"/>。</param>
/// <param name="Message">面向调用方的说明。</param>
public sealed record ApiError(string Code, string Message);

/// <summary>错误码常量。</summary>
public static class ApiErrorCodes
{
    /// <summary>请求本身不合法（缺字段、取值越界等）。</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>用户名或密码错误。</summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>刷新令牌无效、已撤销或已过期。</summary>
    public const string InvalidRefreshToken = "invalid_refresh_token";

    /// <summary>请求过于频繁：登录失败次数过多，已被暂时限流。</summary>
    public const string TooManyRequests = "too_many_requests";

    /// <summary>访问令牌本身可用，但缺少接口所需的身份信息——需要重新登录。</summary>
    public const string InvalidToken = "invalid_token";

    /// <summary>目标账号不存在。</summary>
    public const string AccountNotFound = "account_not_found";

    /// <summary>用户名已被占用。</summary>
    public const string UserNameTaken = "user_name_taken";

    /// <summary>新密码不满足强度要求。</summary>
    public const string WeakPassword = "weak_password";

    /// <summary>这是最后一个启用的账号，停用后无人能登录后台。</summary>
    public const string LastEnabledAccount = "last_enabled_account";

    /// <summary>该操作不允许对操作者自己执行。</summary>
    public const string SelfOperation = "self_operation";

    /// <summary>名单标识或插件类型名对不上任何已知目标。</summary>
    public const string TargetNotFound = "target_not_found";
}

/// <summary>授权策略名。</summary>
public static class AdminPolicies
{
    /// <summary>要求持有 <see cref="Shared.AdminRoles.Admin"/> 角色。</summary>
    public const string Admin = "admin";
}
