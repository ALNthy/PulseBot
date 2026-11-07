using System.Net;
using System.Net.Http.Headers;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 给后台 API 请求附加 Bearer 令牌，并在 401 时自动刷新一次后重放请求。
/// </summary>
/// <remarks>
/// <para>
/// 请求体在发送前先读成字节缓存：HttpRequestMessage 只能发送一次，
/// 重试时必须用同一份内容重建请求，否则 POST/PUT 会在重放时丢失 body。
/// </para>
/// <para>
/// 只重试一次。刷新也失败时把原始 401 交给调用方——再次重试没有意义，
/// 而且会掩盖「登录已失效」这个需要用户重新登录的事实。
/// </para>
/// </remarks>
public sealed class AuthMessageHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;
    private readonly AuthService _auth;

    /// <summary>构造函数注入。</summary>
    /// <param name="tokens">令牌存储。</param>
    /// <param name="auth">认证服务（用于刷新）。</param>
    public AuthMessageHandler(TokenStore tokens, AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(auth);

        _tokens = tokens;
        _auth = auth;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 先缓存请求体：发送之后原请求就不能再用于重放。
        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        ApplyToken(request);

        HttpResponseMessage response = await base
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        bool refreshed = await _auth.TryRefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed)
        {
            return response;
        }

        using HttpRequestMessage retry = Clone(request, body);
        ApplyToken(retry);
        response.Dispose();

        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyToken(HttpRequestMessage request)
    {
        string? token = _tokens.AccessToken;

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>复制请求方法、地址、头与已缓存的请求体。</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);

            if (original.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in original.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return clone;
    }
}
