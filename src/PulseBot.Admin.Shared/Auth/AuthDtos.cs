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

/// <summary>登录尝试的结局。</summary>
public enum LoginOutcome
{
    /// <summary>成功。</summary>
    Success,

    /// <summary>账号不存在、已停用或密码错误——三者刻意不区分。</summary>
    InvalidCredentials,

    /// <summary>失败次数达到阈值，被暂时锁定。</summary>
    Locked,
}

/// <summary>
/// 一次登录尝试的结果。
/// </summary>
/// <remarks>
/// 刻意不用「返回令牌对或 null」表达：调用方需要区分「凭据错误」（401）与
/// 「被限流」（429），把两种失败都压成 <c>null</c> 会迫使接口层去猜。
/// 用静态工厂构造，避免出现 <see cref="LoginOutcome.Success"/> 却没有令牌这类非法组合。
/// </remarks>
/// <param name="Outcome">结局。</param>
/// <param name="Tokens">成功时的令牌对；其余情况为 <c>null</c>。</param>
/// <param name="RetryAfter">被锁定时还需等待多久；其余情况为 <see cref="TimeSpan.Zero"/>。</param>
public sealed record LoginAttemptResult(LoginOutcome Outcome, TokenPairResponse? Tokens, TimeSpan RetryAfter)
{
    /// <summary>登录成功。</summary>
    /// <param name="tokens">签发的令牌对。</param>
    public static LoginAttemptResult Success(TokenPairResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        return new LoginAttemptResult(LoginOutcome.Success, tokens, TimeSpan.Zero);
    }

    /// <summary>凭据错误（含账号不存在与已停用）。</summary>
    public static LoginAttemptResult InvalidCredentials { get; } =
        new(LoginOutcome.InvalidCredentials, null, TimeSpan.Zero);

    /// <summary>被限流锁定；<paramref name="retryAfter"/> 表示还需等待多久。</summary>
    /// <param name="retryAfter">剩余锁定时长。</param>
    public static LoginAttemptResult Locked(TimeSpan retryAfter) =>
        new(LoginOutcome.Locked, null, retryAfter);
}
