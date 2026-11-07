using Microsoft.JSInterop;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 亮色/暗色主题的当前选择。
/// </summary>
/// <remarks>
/// 根组件 <see cref="App"/> 订阅本状态并传给 MudBlazor 的 ThemeProvider；
/// 布局中的切换按钮只改这里，不直接操作 DOM。选择写入 localStorage，
/// 刷新页面后保持；浏览器禁用存储时静默退回内存状态。
/// </remarks>
public sealed class ThemeState
{
    private const string StorageKey = "anbot.admin.theme";

    private readonly IJSRuntime _js;

    /// <summary>构造函数注入 JS 运行时（用于持久化选择）。</summary>
    /// <param name="js">JS 运行时。</param>
    public ThemeState(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        _js = js;
    }

    /// <summary>当前是否为暗色主题。</summary>
    public bool IsDarkMode { get; private set; }

    /// <summary>主题变化通知。</summary>
    public event Action? Changed;

    /// <summary>从本地存储恢复上次的选择。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            string? value = await _js
                .InvokeAsync<string?>("localStorage.getItem", StorageKey)
                .ConfigureAwait(false);

            IsDarkMode = string.Equals(value, "dark", StringComparison.Ordinal);
        }
        catch (JSException)
        {
            // 隐私模式或存储被禁用：保持默认亮色。
        }
    }

    /// <summary>在亮色与暗色之间切换并持久化。</summary>
    public async Task ToggleAsync()
    {
        IsDarkMode = !IsDarkMode;

        try
        {
            await _js
                .InvokeVoidAsync("localStorage.setItem", StorageKey, IsDarkMode ? "dark" : "light")
                .ConfigureAwait(false);
        }
        catch (JSException)
        {
            // 持久化失败不影响当前会话的主题切换。
        }

        Changed?.Invoke();
    }
}
