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
}

/// <summary>授权策略名。</summary>
public static class AdminPolicies
{
    /// <summary>要求持有 <see cref="Shared.AdminRoles.Admin"/> 角色。</summary>
    public const string Admin = "admin";
}
