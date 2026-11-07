using System.Net.Http.Json;
using System.Text.Json;
using AnBot.Admin.Shared.Accounts;
using AnBot.Admin.Shared.Audit;
using AnBot.Admin.Shared.Control;
using AnBot.Admin.Shared.Management;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 后台 REST API 的类型化客户端。
/// </summary>
/// <remarks>
/// 所有方法在非成功响应上抛出 <see cref="ApiException"/>，页面统一 catch 后提示；
/// 成功但响应体为空也视为异常——那说明前后端契约不一致，静默返回默认值只会把问题推迟。
/// </remarks>
public sealed class AdminApiClient
{
    private readonly IHttpClientFactory _httpFactory;

    /// <summary>构造函数注入 HTTP 客户端工厂。</summary>
    /// <param name="httpFactory">工厂；本类使用 <see cref="HttpClientNames.Admin"/> 客户端。</param>
    public AdminApiClient(IHttpClientFactory httpFactory)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        _httpFactory = httpFactory;
    }

    /// <summary>创建带认证处理器的客户端；底层处理器由工厂池化复用。</summary>
    private HttpClient CreateClient() => _httpFactory.CreateClient(HttpClientNames.Admin);

    // ---------- 监控 ----------

    /// <summary>宿主运行状态。</summary>
    public Task<HostStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<HostStatusInfo>("admin/api/status", cancellationToken);

    /// <summary>已注册的适配器。</summary>
    public Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default)
        => GetListAsync<AdapterInfo>("admin/api/adapters", cancellationToken);

    /// <summary>已登记的插件。</summary>
    public Task<IReadOnlyList<PluginInfo>> GetPluginsAsync(CancellationToken cancellationToken = default)
        => GetListAsync<PluginInfo>("admin/api/plugins", cancellationToken);

    /// <summary>可触发的命令。</summary>
    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default)
        => GetListAsync<CommandInfo>("admin/api/commands", cancellationToken);

    /// <summary>定时任务清单。</summary>
    public Task<IReadOnlyList<ScheduledTaskInfo>> GetScheduledTasksAsync(CancellationToken cancellationToken = default)
        => GetListAsync<ScheduledTaskInfo>("admin/api/scheduled-tasks", cancellationToken);

    /// <summary>最近的定时任务执行记录。</summary>
    /// <param name="maxCount">返回条数上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<IReadOnlyList<ScheduledTaskExecutionInfo>> GetExecutionsAsync(
        int maxCount = 50,
        CancellationToken cancellationToken = default)
        => GetListAsync<ScheduledTaskExecutionInfo>(
            $"admin/api/scheduled-tasks/executions?maxCount={maxCount}",
            cancellationToken);

    /// <summary>最近的审计记录。</summary>
    /// <param name="maxCount">返回条数上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<IReadOnlyList<AuditLogInfo>> GetAuditLogsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default)
        => GetListAsync<AuditLogInfo>($"admin/api/audit-logs?maxCount={maxCount}", cancellationToken);

    // ---------- 账号 ----------

    /// <summary>全部后台账号。</summary>
    public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken cancellationToken = default)
        => GetListAsync<AccountInfo>("admin/api/accounts", cancellationToken);

    /// <summary>创建账号。</summary>
    public async Task<AccountInfo> CreateAccountAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PostAsJsonAsync("admin/api/accounts", new CreateAccountRequest(userName, password), cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<AccountInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>修改自己的密码。</summary>
    public async Task<AccountInfo> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PutAsJsonAsync(
                "admin/api/accounts/me/password",
                new ChangePasswordRequest(currentPassword, newPassword),
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<AccountInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重置指定账号的密码。</summary>
    public async Task<AccountInfo> ResetPasswordAsync(
        int id,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PutAsJsonAsync(
                $"admin/api/accounts/{id}/password",
                new ResetPasswordRequest(newPassword),
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<AccountInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启用或停用账号。</summary>
    public async Task<AccountInfo> SetAccountStateAsync(
        int id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PutAsJsonAsync(
                $"admin/api/accounts/{id}/state",
                new SetAccountStateRequest(enabled),
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<AccountInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>撤销指定账号的全部刷新令牌。</summary>
    public async Task<AccountInfo> RevokeSessionsAsync(int id, CancellationToken cancellationToken = default)
    {
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PostAsync($"admin/api/accounts/{id}/revoke-sessions", content: null, cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<AccountInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    // ---------- 运行时控制 ----------

    /// <summary>当前生效的权限名单。</summary>
    public Task<AccessPolicyInfo> GetAccessPolicyAsync(CancellationToken cancellationToken = default)
        => GetAsync<AccessPolicyInfo>("admin/api/access-policy", cancellationToken);

    /// <summary>整体替换一份权限名单。</summary>
    public async Task<AccessPolicyInfo> UpdateAccessPolicyAsync(
        string listKey,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
    {
        string path = $"admin/api/access-policy/{Uri.EscapeDataString(listKey)}";

        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PutAsJsonAsync(path, new UpdatePermissionListRequest(values), cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<AccessPolicyInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启用或停用插件。</summary>
    public async Task SetPluginStateAsync(
        string typeName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        string path = $"admin/api/plugins/{Uri.EscapeDataString(typeName)}/state";

        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .PutAsJsonAsync(path, new SetPluginStateRequest(enabled), cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ---------- 内部 ----------

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await http
            .GetAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken cancellationToken)
    {
        List<T>? items = await GetAsync<List<T>?>(path, cancellationToken).ConfigureAwait(false);
        return items ?? [];
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        T? value = await response.Content
            .ReadFromJsonAsync<T>(cancellationToken)
            .ConfigureAwait(false);

        return value ?? throw new ApiException(response.StatusCode, "invalid_response", "响应内容为空。");
    }

    /// <summary>把非成功响应翻译成带错误码的异常。</summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiErrorResponse? error = null;

        try
        {
            error = await response.Content
                .ReadFromJsonAsync<ApiErrorResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // 非 JSON 响应：保留状态码即可。
        }
        catch (NotSupportedException)
        {
            // Content-Type 不是 JSON。
        }

        throw new ApiException(
            response.StatusCode,
            error?.Code,
            error?.Message ?? $"请求失败（HTTP {(int)response.StatusCode}）。");
    }
}
