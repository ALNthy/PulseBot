using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Shared.Accounts;

namespace AnBot.Admin.Api;

/// <summary>端点共用的响应构造与调用方身份解析。</summary>
/// <remarks>
/// 抽出来是为了让「错误响应的形状」与「操作者是谁」这两件事只有一处定义：
/// 它们分别被全部端点和全部审计记录依赖，各写一份迟早会漂移。
/// </remarks>
internal static class ApiResponses
{
    /// <summary>构造统一形状的失败响应。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">机器可读错误码，取值见 <see cref="ApiErrorCodes"/>。</param>
    /// <param name="message">面向调用方的说明；不得携带堆栈或内部类型名。</param>
    /// <remarks>
    /// 用 <see cref="JsonResult"/> 而不是 <see cref="ObjectResult"/>：后者会参与内容协商，
    /// 当调用方的 <c>Accept</c> 不含 JSON 时会得到 406——错误响应必须始终可解析，
    /// 这与迁移前 Minimal API 的 <c>Results.Json</c> 行为一致。
    /// </remarks>
    public static IActionResult Failure(int statusCode, string code, string message)
        => new JsonResult(new ApiError(code, message)) { StatusCode = statusCode };

    /// <summary>从访问令牌的声明还原操作者身份。</summary>
    /// <param name="http">当前请求。</param>
    /// <param name="actor">解析成功时的操作者。</param>
    /// <returns>声明齐全且格式正确时为 <c>true</c>。</returns>
    /// <remarks>
    /// 这些声明由 <see cref="TokenService"/> 签发，令牌又已通过签名校验，因此正常情况下必然存在。
    /// 仍然显式判断而不是用 <c>!</c> 抑制：一旦将来换了认证方案或 claim 映射，
    /// 这里会返回 401，而不是把解析失败的 0 当成账号 Id 继续往下走——
    /// 那会让「操作者是谁」变成静默的错误值，而它正是审计记录的全部依据。
    /// </remarks>
    public static bool TryGetActor(HttpContext http, [NotNullWhen(true)] out AccountActor? actor)
    {
        ArgumentNullException.ThrowIfNull(http);

        ClaimsPrincipal? principal = http.User;
        string? idText = principal?.FindFirst(TokenService.UserIdClaim)?.Value;
        string? name = principal?.FindFirst(TokenService.NameClaim)?.Value;

        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
            || string.IsNullOrWhiteSpace(name))
        {
            actor = null;
            return false;
        }

        actor = new AccountActor(userId, name, http.Connection.RemoteIpAddress?.ToString());
        return true;
    }
}
