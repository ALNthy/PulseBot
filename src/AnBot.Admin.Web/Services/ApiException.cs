using System.Net;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 后台接口返回的业务错误。
/// </summary>
/// <remarks>
/// 前端不引用服务端程序集（<c>AnBot.Admin.Api</c> 还带着 EF Core 等依赖），
/// 因此错误体的形状在这里单独声明；字段名必须与后端的 <c>ApiError</c> 保持一致。
/// </remarks>
public sealed class ApiException : Exception
{
    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>机器可读错误码；响应不是标准错误体时为 null。</summary>
    public string? Code { get; }

    /// <summary>构造函数。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">机器可读错误码。</param>
    /// <param name="message">面向用户的说明。</param>
    public ApiException(HttpStatusCode statusCode, string? code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}

/// <summary>后端统一错误体的反序列化形状。</summary>
/// <param name="Code">机器可读错误码。</param>
/// <param name="Message">面向用户的说明。</param>
internal sealed record ApiErrorResponse(string Code, string Message);
