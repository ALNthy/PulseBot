using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 基于令牌存储的认证状态提供者。
/// </summary>
/// <remarks>
/// 浏览器的身份完全由「是否持有令牌」决定，因此这里不解析 JWT 声明，
/// 只在登录成功、刷新成功与登出时切换状态并通知 Blazor 重新求值路由授权。
/// </remarks>
public sealed class AuthStateProvider : AuthenticationStateProvider
{
    private readonly TokenStore _tokens;

    /// <summary>构造函数注入令牌存储。</summary>
    /// <param name="tokens">令牌存储。</param>
    public AuthStateProvider(TokenStore tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(CreateState());

    /// <summary>令牌状态变化后通知 Blazor 重新求值。</summary>
    public void NotifyChanged()
        => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private AuthenticationState CreateState()
    {
        if (_tokens.Tokens is null)
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, _tokens.UserName ?? "admin") },
            authenticationType: "jwt");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
