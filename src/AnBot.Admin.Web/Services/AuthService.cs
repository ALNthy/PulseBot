using System.Net.Http.Json;
using System.Text.Json;
using AnBot.Admin.Shared.Auth;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 登录、刷新与登出。
/// </summary>
/// <remarks>
/// <para>
/// 刷新请求使用<strong>不带认证处理器</strong>的 HttpClient：否则刷新失败得到 401 时，
/// 处理器会再次尝试刷新，形成递归。
/// </para>
/// <para>
/// 并发刷新用信号量串行化并做双重检查：多个请求同时收到 401 时只发起一次刷新，
/// 其余请求等待后直接使用新令牌。刷新令牌是轮换的，并发刷新会让除一个以外的请求全部失败。
/// </para>
/// </remarks>
public sealed class AuthService
{
    private readonly TokenStore _tokens;
    private readonly AuthStateProvider _state;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>构造函数注入。</summary>
    /// <param name="tokens">令牌存储。</param>
    /// <param name="state">认证状态提供者。</param>
    /// <param name="httpFactory">HTTP 客户端工厂（使用 <see cref="HttpClientNames.Auth"/>）。</param>
    public AuthService(TokenStore tokens, AuthStateProvider state, IHttpClientFactory httpFactory)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(httpFactory);

        _tokens = tokens;
        _state = state;
        _httpFactory = httpFactory;
    }

    /// <summary>用账号密码登录；失败时抛出 <see cref="ApiException"/>。</summary>
    /// <param name="userName">用户名。</param>
    /// <param name="password">密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        using HttpClient http = _httpFactory.CreateClient(HttpClientNames.Auth);
        using HttpResponseMessage response = await http
            .PostAsJsonAsync("admin/api/auth/login", new LoginRequest(userName, password), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        TokenPairResponse? tokens = await response.Content
            .ReadFromJsonAsync<TokenPairResponse>(cancellationToken)
            .ConfigureAwait(false);

        if (tokens is null)
        {
            throw new ApiException(response.StatusCode, "invalid_response", "登录成功但响应中没有令牌。");
        }

        await _tokens.SaveAsync(tokens).ConfigureAwait(false);
        _state.NotifyChanged();
    }

    /// <summary>从本地存储恢复登录态；没有刷新令牌时什么都不做。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        await _tokens.LoadAsync().ConfigureAwait(false);

        if (_tokens.RefreshToken is null)
        {
            return;
        }

        await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用刷新令牌换取新令牌对；失败时清除登录态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>刷新成功（或并发请求已完成刷新）时为 true。</returns>
    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default)
    {
        string? refreshToken = _tokens.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            return false;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 等待锁期间其他请求可能已经刷新过：此时直接用新令牌，不再消费旧刷新令牌。
            if (!string.Equals(_tokens.RefreshToken, refreshToken, StringComparison.Ordinal)
                && _tokens.AccessToken is not null)
            {
                return true;
            }

            using HttpClient http = _httpFactory.CreateClient(HttpClientNames.Auth);
            using HttpResponseMessage response = await http
                .PostAsJsonAsync("admin/api/auth/refresh", new RefreshTokenRequest(refreshToken), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await ClearSessionAsync().ConfigureAwait(false);
                return false;
            }

            TokenPairResponse? tokens = await response.Content
                .ReadFromJsonAsync<TokenPairResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (tokens is null)
            {
                await ClearSessionAsync().ConfigureAwait(false);
                return false;
            }

            await _tokens.SaveAsync(tokens).ConfigureAwait(false);
            _state.NotifyChanged();
            return true;
        }
        catch (HttpRequestException)
        {
            // 网络中断：保留登录态，等下一次请求再试。
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>撤销刷新令牌并清除本地登录态；网络失败也照常登出。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        string? refreshToken = _tokens.RefreshToken;

        if (!string.IsNullOrEmpty(refreshToken))
        {
            try
            {
                using HttpClient http = _httpFactory.CreateClient(HttpClientNames.Auth);
                using HttpResponseMessage ignored = await http
                    .PostAsJsonAsync("admin/api/auth/logout", new RefreshTokenRequest(refreshToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // 离线时也允许本地登出：服务端的令牌会自然过期。
            }
        }

        await ClearSessionAsync().ConfigureAwait(false);
    }

    private async Task ClearSessionAsync()
    {
        await _tokens.ClearAsync().ConfigureAwait(false);
        _state.NotifyChanged();
    }

    /// <summary>把非成功响应翻译成带错误码的异常。</summary>
    private static async Task<ApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ApiErrorResponse? error = null;

        try
        {
            error = await response.Content
                .ReadFromJsonAsync<ApiErrorResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // 非 JSON 响应（例如代理返回的 502 页面）：保留状态码即可。
        }
        catch (NotSupportedException)
        {
            // Content-Type 不是 JSON。
        }

        return new ApiException(
            response.StatusCode,
            error?.Code,
            error?.Message ?? $"请求失败（HTTP {(int)response.StatusCode}）。");
    }
}
