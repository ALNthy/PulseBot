using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PulseBot.Adapter.Console;
using PulseBot.Adapter.OneBot.V12;
using PulseBot.Admin.Api;
using PulseBot.Admin.Api.Notifications;
using PulseBot.Admin.Infrastructure;
using PulseBot.Admin.Shared;
using PulseBot.Cli;
using PulseBot.Core;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;
using PulseBot.Core.Scheduling;
using PulseBot.Core.Sessions;
using PulseBot.Plugins.Echo;

// Microsoft.AspNetCore.Builder 命名空间里也有一个 SessionOptions（HTTP 会话中间件的选项），
// 与 PulseBot 的交互会话配置同名。用别名消歧，避免两处 SessionOptions 变成歧义引用。
using SessionOptions = PulseBot.Core.Sessions.SessionOptions;

// PulseBot 宿主入口：装配内核 + 适配器 + 插件 + 后台，然后常驻运行直到收到关闭信号（Ctrl+C）。
//
// 插件与适配器都在编译期显式登记（项目引用），不做运行时 DLL 扫描：
// 引用关系由编译器校验，部署时也不会有「插件目录里少了一个 dll」这类运行期故障。
//
// 用 WebApplication 而不是 Host：后台管理与机器人同进程运行，共用这一个 DI 容器，
// 因此 IManagementService 能看到真实的适配器、插件与调度器状态。
// 显式指定内容根目录：默认取「当前工作目录」，那样从仓库根执行 dotnet run 时
// 会去仓库根找 appsettings.json（不存在），配置被静默忽略。
// 以可执行文件所在目录为准，配置文件才能稳定随程序一起被找到。
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// 内核：事件分发、命令分发、插件系统、消息发送。
builder.Services.AddPulseBot();

// 配置绑定：没有这一步，appsettings.json 里的权限策略与适配器配置不会生效。
builder.Services.Configure<PermissionOptions>(
    builder.Configuration.GetSection(PermissionOptions.SectionName));
builder.Services.Configure<ConsoleAdapterOptions>(
    builder.Configuration.GetSection(ConsoleAdapterOptions.SectionName));
builder.Services.Configure<SessionOptions>(
    builder.Configuration.GetSection(SessionOptions.SectionName));
builder.Services.Configure<SchedulingOptions>(
    builder.Configuration.GetSection(SchedulingOptions.SectionName));

// 插件：逐个显式登记，由 PluginManager 在启动时统一加载并扫描 [OnCommand]。
builder.Services.AddPlugin<EchoPlugin>();

// 定时任务：与插件一样逐个显式登记（任务不必是插件）。
// builder.Services.AddScheduledTask<DailyReportTask>();

// 适配器：控制台适配器把标准输入输出当作聊天平台，便于本地联调与插件开发；
// OneBot 12 适配器以反向 WebSocket 接入真实平台——本进程监听端口，OneBot 实现端主动连入。
builder.Services.AddConsoleAdapter();
builder.Services.Configure<OneBot12Options>(
    builder.Configuration.GetSection(OneBot12Options.SectionName));
builder.Services.AddOneBot12Adapter();

// 后台管理：数据访问、认证、审计，以及只读监控端点的实现。
// 配置非法（例如启用后台却没配签名密钥）会在下面的 Build() 阶段直接失败。
builder.Services.Configure<AdminOptions>(
    builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.AddAdminInfrastructure();
builder.Services.AddAdminApi();

// 后台的实时事件流：把每条进入管道的事件复制一份摘要推给后台。
// 挂在 Core 的中间件扩展点上，具体位置由中间件自身的优先级决定（见 AdminEventMirror.Priority）。
builder.Services.AddEventMiddleware<AdminEventMirror>();

// 监听地址要在 Build 之前交给 Kestrel。这里读一次配置用于装配期决策，
// 运行期各服务一律通过 IOptions<AdminOptions> 取值（同上方的 Configure 调用）。
AdminOptions adminOptions =
    builder.Configuration.GetSection(AdminOptions.SectionName).Get<AdminOptions>() ?? new AdminOptions();

if (adminOptions.Enabled)
{
    builder.WebHost.UseUrls(adminOptions.Url);
}

// 把插件与适配器的启停挂到宿主生命周期上。
builder.Services.AddHostedService<PulseBotLifecycleService>();

// 交互会话的空闲回收：Core 不做后台任务，由宿主按配置间隔驱动。
builder.Services.AddHostedService<SessionCleanupService>();

// 定时任务调度：同样由宿主驱动。注册在最后 → 关闭时最先停止，不再拉起新一轮任务。
builder.Services.AddHostedService<SchedulerHostedService>();

WebApplication app = builder.Build();

// 挂载后台接口；Admin:Enabled 为 false 时本调用不做任何事。
app.MapAdminApi();

await app.RunAsync();
