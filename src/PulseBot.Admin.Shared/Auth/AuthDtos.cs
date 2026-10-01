namespace PulseBot.Admin.Shared.Auth;

/// <summary>登录请求。</summary>
/// <param name="UserName">用户名。</param>
/// <param name="Password">密码明文；仅在本次请求内存中存在，服务端只保存其派生哈希。</param>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>刷新令牌请求。</summary>
/// <param name="RefreshToken">上一次登录（或刷新）返回的刷新令牌。</param>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>
/// 登录/刷新成功后的令牌对。
/// </summary>
/// <param name="AccessToken">访问令牌（JWT），放入 <c>Authorization: Bearer</c> 头。</param>
/// <param name="AccessTokenExpiresAt">访问令牌到期时刻。</param>
/// <param name="RefreshToken">刷新令牌。每次刷新都会轮换，旧值立即失效。</param>
/// <param name="RefreshTokenExpiresAt">刷新令牌到期时刻。</param>
/// <param name="UserName">账号名，供界面直接展示。</param>
public sealed record TokenPairResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string UserName);
