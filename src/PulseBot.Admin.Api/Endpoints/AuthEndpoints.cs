using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PulseBot.Admin.Infrastructure.Auth;
using PulseBot.Admin.Shared.Auth;

namespace PulseBot.Admin.Api.Endpoints;

/// <summary>认证端点：登录、刷新令牌、登出。</summary>
internal static class AuthEndpoints
{
    /// <summary>把认证端点挂到分组上。</summary>
    /// <param name="group">后台 API 分组（前缀 <see cref="AdminApiEndpointRouteBuilderExtensions.RoutePrefix"/>）。</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/auth/login", LoginAsync)
            .WithSummary("用账号密码换取访问令牌与刷新令牌")
            .AllowAnonymous();

        group.MapPost("/auth/refresh", RefreshAsync)
            .WithSummary("用刷新令牌换取新的令牌对（旧刷新令牌立即作废）")
            .AllowAnonymous();

        group.MapPost("/auth/logout", LogoutAsync)
            .WithSummary("撤销一个刷新令牌")
            .AllowAnonymous();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IAuthenticationService authentication,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "用户名与密码均不能为空。"));
        }

        LoginAttemptResult result = await authentication
            .LoginAsync(request.UserName, request.Password, GetRemoteIp(http), cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                // Outcome 为 Success 时 Tokens 一定非空，由 LoginAttemptResult.Success 保证。
                return Results.Ok(result.Tokens);

            case LoginOutcome.Locked:
                // 向上取整：向下取整会让「还剩 0.4 秒」变成 Retry-After: 0，
                // 客户端立刻重试又被拒，看起来像限流没有生效。
                int retryAfterSeconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);
                http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                return Results.Json(
                    new ApiError(
                        ApiErrorCodes.TooManyRequests,
                        $"登录失败次数过多，请在 {retryAfterSeconds} 秒后重试。"),
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                return Results.Json(
                    new ApiError(ApiErrorCodes.InvalidCredentials, "用户名或密码错误。"),
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> RefreshAsync(
        RefreshTokenRequest request,
        IAuthenticationService authentication,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "刷新令牌不能为空。"));
        }

        TokenPairResponse? tokens = await authentication
            .RefreshAsync(request.RefreshToken, GetRemoteIp(http), cancellationToken)
            .ConfigureAwait(false);

        return tokens is null
            ? Results.Json(
                new ApiError(ApiErrorCodes.InvalidRefreshToken, "刷新令牌无效、已撤销或已过期。"),
                statusCode: StatusCodes.Status401Unauthorized)
            : Results.Ok(tokens);
    }

    private static async Task<IResult> LogoutAsync(
        RefreshTokenRequest request,
        IAuthenticationService authentication,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (request is not null && !string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await authentication
                .LogoutAsync(request.RefreshToken, GetRemoteIp(http), cancellationToken)
                .ConfigureAwait(false);
        }

        // 无论令牌是否有效都回 204：登出是幂等操作，而且「这个令牌存不存在」
        // 不该成为未认证调用方能探测到的信息。
        return Results.NoContent();
    }

    /// <summary>取调用方 IP，用于审计。</summary>
    private static string? GetRemoteIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
