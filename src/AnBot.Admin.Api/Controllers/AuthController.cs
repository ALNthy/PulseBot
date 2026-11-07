using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Shared.Auth;

namespace AnBot.Admin.Api.Controllers;

/// <summary>认证控制器：登录、刷新令牌、登出。</summary>
/// <remarks>
/// 这三个端点匿名可达——登录本身就是取得令牌的入口，自然不能先要求令牌。
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route(AdminApiEndpointRouteBuilderExtensions.RoutePrefix + "/auth")]
public sealed class AuthController : ControllerBase
{
    /// <summary>认证服务，负责校验凭据、签发与轮换令牌。</summary>
    private readonly IAuthenticationService _authentication;

    /// <summary>构造函数注入认证服务。</summary>
    /// <param name="authentication">认证服务。</param>
    public AuthController(IAuthenticationService authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        _authentication = authentication;
    }

    /// <summary>用账号密码换取访问令牌与刷新令牌。</summary>
    /// <param name="request">账号与密码。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>成功时为令牌对；凭据错误为 401；失败次数过多为 429 并带 <c>Retry-After</c> 头。</returns>
    [HttpPost("login")]
    public async Task<IActionResult> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "用户名与密码均不能为空。");
        }

        LoginAttemptResult result = await _authentication
            .LoginAsync(request.UserName, request.Password, GetRemoteIp(), cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                // Outcome 为 Success 时 Tokens 一定非空，由 LoginAttemptResult.Success 保证。
                return Ok(result.Tokens);

            case LoginOutcome.Locked:
                // 向上取整：向下取整会让「还剩 0.4 秒」变成 Retry-After: 0，
                // 客户端立刻重试又被拒，看起来像限流没有生效。
                int retryAfterSeconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);
                Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                return ApiResponses.Failure(
                    StatusCodes.Status429TooManyRequests,
                    ApiErrorCodes.TooManyRequests,
                    $"登录失败次数过多，请在 {retryAfterSeconds} 秒后重试。");

            default:
                return ApiResponses.Failure(
                    StatusCodes.Status401Unauthorized,
                    ApiErrorCodes.InvalidCredentials,
                    "用户名或密码错误。");
        }
    }

    /// <summary>用刷新令牌换取新的令牌对（旧刷新令牌立即作废）。</summary>
    /// <param name="request">刷新令牌。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>成功时为新的令牌对；令牌无效、已撤销或已过期为 401。</returns>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "刷新令牌不能为空。");
        }

        TokenPairResponse? tokens = await _authentication
            .RefreshAsync(request.RefreshToken, GetRemoteIp(), cancellationToken)
            .ConfigureAwait(false);

        return tokens is null
            ? ApiResponses.Failure(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidRefreshToken,
                "刷新令牌无效、已撤销或已过期。")
            : Ok(tokens);
    }

    /// <summary>撤销一个刷新令牌。</summary>
    /// <param name="request">要撤销的刷新令牌；请求体缺省或令牌为空时视为无事可做。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>恒为 204。</returns>
    /// <remarks>
    /// 无论令牌是否有效都回 204：登出是幂等操作，而且「这个令牌存不存在」
    /// 不该成为未认证调用方能探测到的信息。
    /// </remarks>
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (request is not null && !string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await _authentication
                .LogoutAsync(request.RefreshToken, GetRemoteIp(), cancellationToken)
                .ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>取调用方 IP，用于审计。</summary>
    private string? GetRemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
