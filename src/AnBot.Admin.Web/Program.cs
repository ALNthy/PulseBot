using AnBot.Admin.Web;
using AnBot.Admin.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// 空值表示与后台同源部署（静态文件由宿主托管）；开发期由 appsettings.Development.json
// 指向 http://localhost:5387，因此需要宿主开启对应的 CORS 策略。
string configuredApiBase = builder.Configuration["ApiBaseUrl"] ?? string.Empty;

Uri baseAddress = string.IsNullOrWhiteSpace(configuredApiBase)
    ? new Uri(builder.HostEnvironment.BaseAddress)
    : new Uri(configuredApiBase.TrimEnd('/') + "/");

builder.Services.AddSingleton<ThemeState>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<AuthStateProvider>();
builder.Services.AddSingleton<AuthenticationStateProvider>(
    static provider => provider.GetRequiredService<AuthStateProvider>());

// 认证处理器由工厂按请求解析；它依赖 AuthService（单例），而 AuthService 使用的是
// 不带该处理器的 Auth 客户端，因此不会形成依赖环。
builder.Services.AddTransient<AuthMessageHandler>();

// 登录与刷新使用不带认证处理器的客户端：否则刷新失败时处理器会再次触发刷新，形成递归。
builder.Services.AddHttpClient(HttpClientNames.Auth, client => client.BaseAddress = baseAddress);

// 业务客户端：自动附加 Bearer，401 时刷新一次并重放。
builder.Services.AddHttpClient(HttpClientNames.Admin, client => client.BaseAddress = baseAddress)
    .AddHttpMessageHandler<AuthMessageHandler>();

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AdminApiClient>();

builder.Services.AddSingleton(provider => new AdminHubClient(
    provider.GetRequiredService<TokenStore>(),
    baseAddress));

WebAssemblyHost host = builder.Build();

// 恢复上次的主题选择；随后用本地存储里的刷新令牌恢复登录态（刷新页面后内存令牌会丢失）。
await host.Services.GetRequiredService<ThemeState>().InitializeAsync();
await host.Services.GetRequiredService<AuthService>().TryRestoreAsync();

await host.RunAsync();
