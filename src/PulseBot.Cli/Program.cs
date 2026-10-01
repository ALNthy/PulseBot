using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseBot.Adapter.Console;
using PulseBot.Cli;
using PulseBot.Core;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;
using PulseBot.Core.Scheduling;
using PulseBot.Core.Sessions;
using PulseBot.Plugins.Echo;

// PulseBot 宿主入口：装配内核 + 适配器 + 插件，然后常驻运行直到收到关闭信号（Ctrl+C）。
//
// 插件与适配器都在编译期显式登记（项目引用），不做运行时 DLL 扫描：
// 引用关系由编译器校验，部署时也不会有「插件目录里少了一个 dll」这类运行期故障。
// 显式指定内容根目录：默认取「当前工作目录」，那样从仓库根执行 dotnet run 时
// 会去仓库根找 appsettings.json（不存在），配置被静默忽略。
// 以可执行文件所在目录为准，配置文件才能稳定随程序一起被找到。
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
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
// 接入真实平台时在此登记（每个平台一个）：
// builder.Services.AddSingleton<IAdapter, OneBotAdapter>();
builder.Services.AddConsoleAdapter();

// 把插件与适配器的启停挂到宿主生命周期上。
builder.Services.AddHostedService<PulseBotLifecycleService>();

// 交互会话的空闲回收：Core 不做后台任务，由宿主按配置间隔驱动。
builder.Services.AddHostedService<SessionCleanupService>();

// 定时任务调度：同样由宿主驱动。注册在最后 → 关闭时最先停止，不再拉起新一轮任务。
builder.Services.AddHostedService<SchedulerHostedService>();

IHost host = builder.Build();

await host.RunAsync();
