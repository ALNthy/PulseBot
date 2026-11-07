using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AnBot.Admin.Api;
using AnBot.Admin.Shared.Auth;
using AnBot.Admin.Shared.Control;

namespace AnBot.Admin.Tests.Api;

/// <summary>
/// 后台 REST API 的端到端集成测试：覆盖路由映射、JWT 认证、模型绑定、
/// 统一错误形状，以及账号/名单等写操作的主路径。
/// </summary>
/// <remarks>
/// 这些测试跑在真实的 ASP.NET Core 管道上（见 <see cref="AdminApiTestHost"/>），
/// 与单元测试互补：单元测试验证服务层规则，这里验证「规则确实被接到了 HTTP 上」。
/// 标注 <c>Category=Integration</c>，可按类别单独过滤。
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AdminApiIntegrationTests : IClassFixture<AdminApiFixture>
{
    private readonly AdminApiFixture _fixture;

    /// <summary>接收共享的测试宿主。</summary>
    /// <param name="fixture">类夹具。</param>
    public AdminApiIntegrationTests(AdminApiFixture fixture) => _fixture = fixture;

    private AdminApiTestHost Host => _fixture.Host;

    private HttpClient Client => Host.Client;

    /// <summary>健康探针匿名可达，返回固定标记。</summary>
    [Fact]
    public async Task Health_WithoutAuthentication_ReturnsHealthy()
    {
        HttpResponseMessage response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>未携带令牌访问受保护端点应得到 401。</summary>
    [Fact]
    public async Task Status_WithoutToken_ReturnsUnauthorized()
    {
        HttpResponseMessage response = await Client.GetAsync("/admin/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>引导账号可以登录并拿到访问令牌。</summary>
    [Fact]
    public async Task Login_WithBootstrapCredentials_ReturnsTokenPair()
    {
        string accessToken = await Host.LoginAsync();

        Assert.False(string.IsNullOrWhiteSpace(accessToken));
    }

    /// <summary>密码错误返回 401，且错误体是统一的 ApiError 形状。</summary>
    [Fact]
    public async Task Login_WithWrongPassword_ReturnsInvalidCredentials()
    {
        HttpResponseMessage response = await Client.PostAsJsonAsync(
            "/admin/api/auth/login",
            new { userName = AdminApiTestHost.BootstrapUser, password = "definitely-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        ApiError? error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, error.Code);
    }

    /// <summary>
    /// 非法 JSON 由 [ApiController] 的模型绑定拦截，并统一翻译成 ApiError——
    /// 而不是 MVC 默认的 ProblemDetails。
    /// </summary>
    [Fact]
    public async Task MalformedJson_ReturnsUnifiedApiError()
    {
        string token = await Host.LoginAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("{not-json", Encoding.UTF8, "application/json");

        HttpResponseMessage response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiError? error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal(ApiErrorCodes.InvalidRequest, error.Code);
    }

    /// <summary>所有只读监控端点在有令牌时都应返回 200。</summary>
    [Theory]
    [InlineData("/admin/api/status")]
    [InlineData("/admin/api/adapters")]
    [InlineData("/admin/api/plugins")]
    [InlineData("/admin/api/commands")]
    [InlineData("/admin/api/scheduled-tasks")]
    [InlineData("/admin/api/scheduled-tasks/executions?maxCount=5")]
    [InlineData("/admin/api/audit-logs?maxCount=5")]
    [InlineData("/admin/api/access-policy")]
    [InlineData("/admin/api/accounts")]
    public async Task MonitoringEndpoints_WithToken_ReturnOk(string path)
    {
        string token = await Host.LoginAsync();

        HttpResponseMessage response = await Client.SendAsync(
            AdminApiTestHost.CreateAuthorizedRequest(HttpMethod.Get, path, token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>创建账号成功；同名再创建返回 409 与 userNameTaken 错误码。</summary>
    [Fact]
    public async Task CreateAccount_ThenDuplicate_ReturnsConflict()
    {
        string token = await Host.LoginAsync();
        string userName = $"it-user-{Guid.NewGuid():N}";

        HttpResponseMessage created = await Client.SendAsync(
            AdminApiTestHost.CreateAuthorizedRequest(
                HttpMethod.Post,
                "/admin/api/accounts",
                token,
                new { userName, password = "it-password-123" }));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        HttpResponseMessage duplicate = await Client.SendAsync(
            AdminApiTestHost.CreateAuthorizedRequest(
                HttpMethod.Post,
                "/admin/api/accounts",
                token,
                new { userName, password = "it-password-123" }));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        ApiError? error = await duplicate.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal(ApiErrorCodes.UserNameTaken, error.Code);
    }

    /// <summary>不存在的账号返回 404 与 accountNotFound 错误码。</summary>
    [Fact]
    public async Task SetAccountState_UnknownId_ReturnsNotFound()
    {
        string token = await Host.LoginAsync();

        HttpResponseMessage response = await Client.SendAsync(
            AdminApiTestHost.CreateAuthorizedRequest(
                HttpMethod.Put,
                "/admin/api/accounts/999999/state",
                token,
                new { enabled = false }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiError? error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal(ApiErrorCodes.AccountNotFound, error.Code);
    }

    /// <summary>名单写入立即生效，并回读当前权威策略。</summary>
    [Fact]
    public async Task UpdateAccessPolicy_ReturnsUpdatedPolicy()
    {
        string token = await Host.LoginAsync();

        HttpResponseMessage response = await Client.SendAsync(
            AdminApiTestHost.CreateAuthorizedRequest(
                HttpMethod.Put,
                "/admin/api/access-policy/super-admins",
                token,
                new { values = Array.Empty<string>() }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AccessPolicyInfo? policy = await response.Content.ReadFromJsonAsync<AccessPolicyInfo>();
        Assert.NotNull(policy);
        Assert.Contains(policy.Lists, list => list.Key == "super-admins" && list.Values.Count == 0);
    }

    /// <summary>刷新令牌会轮换旧值；登出返回 204 且幂等。</summary>
    [Fact]
    public async Task RefreshAndLogout_Work()
    {
        HttpResponseMessage login = await Client.PostAsJsonAsync(
            "/admin/api/auth/login",
            new { userName = AdminApiTestHost.BootstrapUser, password = AdminApiTestHost.BootstrapPassword });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        TokenPairResponse? tokens = await login.Content.ReadFromJsonAsync<TokenPairResponse>();
        Assert.NotNull(tokens);

        HttpResponseMessage refreshed = await Client.PostAsJsonAsync(
            "/admin/api/auth/refresh",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        TokenPairResponse? rotated = await refreshed.Content.ReadFromJsonAsync<TokenPairResponse>();
        Assert.NotNull(rotated);
        Assert.NotEqual(tokens.RefreshToken, rotated.RefreshToken);

        HttpResponseMessage logout = await Client.PostAsJsonAsync(
            "/admin/api/auth/logout",
            new { refreshToken = rotated.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }
}
