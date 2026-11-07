using System.Text.Json;
using AnBot.Admin.Shared.Auth;
using Microsoft.JSInterop;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 令牌存储器：访问令牌只留在内存，刷新令牌与用户名落到浏览器本地存储。
/// </summary>
/// <remarks>
/// <para>
/// 刷新页面后访问令牌必然丢失，客户端用持久化的刷新令牌换一对新令牌即可恢复登录；
/// 把访问令牌也写进 localStorage 会扩大 XSS 的利用窗口，收益却只是省一次刷新请求。
/// </para>
/// <para>
/// <strong>安全取舍</strong>：localStorage 中的刷新令牌是明文。Blazor WebAssembly 没有
/// HttpOnly Cookie 可用（前端本身持有令牌），因此这是绕不开的暴露面。后台应置于可信环境，
/// 配合严格的 CSP，并尽量缩短刷新令牌有效期。去掉持久化（只存内存）可以完全消除这一暴露，
/// 代价是每次刷新页面都要重新登录。
/// </para>
/// </remarks>
public sealed class TokenStore
{
    /// <summary>本地存储的键名。</summary>
    private const string StorageKey = "anbot.admin.auth";

    private readonly IJSRuntime _js;

    /// <summary>构造函数注入 JS 运行时（用于访问 localStorage）。</summary>
    /// <param name="js">JS 运行时。</param>
    public TokenStore(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        _js = js;
    }

    /// <summary>当前的令牌对；未登录时为 null。</summary>
    public TokenPairResponse? Tokens { get; private set; }

    /// <summary>当前访问令牌；未登录时为 null。</summary>
    public string? AccessToken => Tokens?.AccessToken;

    /// <summary>当前刷新令牌；未登录时为 null。</summary>
    public string? RefreshToken => Tokens?.RefreshToken;

    /// <summary>当前登录用户名。</summary>
    public string? UserName { get; private set; }

    /// <summary>从本地存储恢复令牌对；内容损坏或不存在时按未登录处理。</summary>
    public async Task LoadAsync()
    {
        try
        {
            string? json = await _js
                .InvokeAsync<string?>("localStorage.getItem", StorageKey)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            StoredAuth? stored = JsonSerializer.Deserialize<StoredAuth>(json);

            if (stored is not null)
            {
                Tokens = stored.Tokens;
                UserName = stored.UserName;
            }
        }
        catch (JSException)
        {
            // 浏览器禁用存储或隐私模式：按未登录处理。
        }
        catch (JsonException)
        {
            // 旧版本残留或手动改坏的内容：丢弃即可。
            await ClearAsync().ConfigureAwait(false);
        }
    }

    /// <summary>保存令牌对并持久化刷新令牌。</summary>
    /// <param name="tokens">新签发的令牌对。</param>
    public async Task SaveAsync(TokenPairResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        Tokens = tokens;
        UserName = tokens.UserName;

        string json = JsonSerializer.Serialize(new StoredAuth(tokens, tokens.UserName));
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json).ConfigureAwait(false);
    }

    /// <summary>清空内存与本地存储中的令牌。</summary>
    public async Task ClearAsync()
    {
        Tokens = null;
        UserName = null;
        await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey).ConfigureAwait(false);
    }

    /// <summary>持久化的内容：整个令牌对加用户名。</summary>
    /// <remarks>
    /// 刷新令牌会轮换，因此必须连同有效期一起保存；只存刷新令牌字符串无法判断是否已过期。
    /// </remarks>
    private sealed record StoredAuth(TokenPairResponse Tokens, string UserName);
}
