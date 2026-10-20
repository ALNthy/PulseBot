using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PulseBot.Admin.Api;
using PulseBot.Admin.Infrastructure;
using PulseBot.Admin.Shared;
using PulseBot.Admin.Shared.Auth;
using PulseBot.Core;

namespace PulseBot.Admin.Tests.Api;

/// <summary>
/// 后台 REST API 的集成测试宿主：在进程内跑真实的 ASP.NET Core 管道
/// （路由、JWT 认证、模型绑定、控制器、错误形状），数据库用临时文件 SQLite。
/// </summary>
/// <remarks>
/// <para>
/// 不用 <c>WebApplicationFactory&lt;Program&gt;</c>：Program 是完整的 Cli 宿主，
/// 会连带启动控制台适配器、OneBot 监听与调度器；测试只需要后台这一层。
/// 这里按 Cli 的装配顺序取用 Admin 相关服务，得到的仍是同一条真实请求管道。
/// </para>
/// <para>
/// 数据库落到临时文件而不是 <c>:memory:</c>：<see cref="AdminDatabaseInitializer"/> 在启动时
/// 建库并创建引导账号，而 DbContextFactory 会为每次操作新建连接——内存库随连接消失，
/// 无法跨请求共享。
/// </para>
/// <para>
/// TestServer 不绑定任何端口，多个测试宿主可以并行共存；每个宿主各用一份临时数据库，
/// 互不干扰。
/// </para>
/// </remarks>
public sealed class AdminApiTestHost : IAsyncDisposable
{
    /// <summary>引导账号用户名。</summary>
    public const string BootstrapUser = "it-admin";

    /// <summary>引导账号初始密码。</summary>
    public const string BootstrapPassword = "it-password-123";

    /// <summary>HTTP 客户端，BaseAddress 指向 TestServer。</summary>
    public HttpClient Client { get; }

    private readonly WebApplication _app;
    private readonly string _databasePath;

    private AdminApiTestHost(WebApplication app, string databasePath, HttpClient client)
    {
        _app = app;
        _databasePath = databasePath;
        Client = client;
    }

    /// <summary>启动宿主；返回时建库与引导账号创建均已完成。</summary>
    public static async Task<AdminApiTestHost> StartAsync()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"pulsebot-admin-api-test-{Guid.NewGuid():N}.db");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // 不读取仓库里的 appsettings：测试所需的配置全部在下面显式给出。
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();

        // 只保留警告及以上：避免 EF Core 与认证中间件的 Info 日志淹没测试输出。
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.Configure<AdminOptions>(options =>
        {
            options.Enabled = true;
            options.DatabasePath = databasePath;
            options.Auth.SigningKey = "integration-test-signing-key-0123456789abcdef";
            options.Auth.BootstrapUser = BootstrapUser;
            options.Auth.BootstrapPassword = BootstrapPassword;

            // 同一个宿主被整个测试类共享：调高阈值，避免若干次失败登录互相触发锁定。
            options.Throttle.MaxFailuresPerUser = 100;
            options.Throttle.MaxFailuresPerIp = 1000;
        });

        // Core 内核：RuntimeControlInitializer 需要权限策略、插件状态注册表与插件管理器。
        builder.Services.AddPulseBot();
        builder.Services.AddAdminInfrastructure();
        builder.Services.AddAdminApi();

        WebApplication app = builder.Build();
        app.MapAdminApi();

        await app.StartAsync().ConfigureAwait(false);

        return new AdminApiTestHost(app, databasePath, app.GetTestClient());
    }

    /// <summary>用引导账号登录并返回访问令牌。</summary>
    public async Task<string> LoginAsync()
    {
        HttpResponseMessage response = await Client
            .PostAsJsonAsync("/admin/api/auth/login", new { userName = BootstrapUser, password = BootstrapPassword })
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        TokenPairResponse? tokens = await response.Content
            .ReadFromJsonAsync<TokenPairResponse>()
            .ConfigureAwait(false);

        return tokens?.AccessToken
            ?? throw new InvalidOperationException("登录成功但响应中没有访问令牌。");
    }

    /// <summary>创建带 Bearer 头的请求；不修改共享 Client 的默认头，避免测试间互相影响。</summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="accessToken">访问令牌。</param>
    /// <param name="body">可选 JSON 请求体。</param>
    public static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string path,
        string accessToken,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);

        // 连同 SQLite 的 WAL/SHM 一起清理：只删主文件会留下 -wal/-shm 残留。
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略：临时目录由操作系统回收，不影响测试结论。
            }
        }
    }
}

/// <summary>让整个测试类共享一个宿主（xUnit 的类夹具）。</summary>
public sealed class AdminApiFixture : IAsyncLifetime
{
    /// <summary>已启动的测试宿主。</summary>
    public AdminApiTestHost Host { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync() => Host = await AdminApiTestHost.StartAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await Host.DisposeAsync();
}
