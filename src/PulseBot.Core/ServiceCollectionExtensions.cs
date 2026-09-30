using Microsoft.Extensions.DependencyInjection;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Plugins;

namespace PulseBot.Core;

/// <summary>
/// PulseBot 内核的总装配入口。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 PulseBot 内核的全部服务：事件分发、命令分发、权限与接入控制、插件系统与消息发送。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 适配器与插件由宿主显式注册——采用编译期项目引用，不做运行时 DLL 扫描：
    /// </para>
    /// <code>
    /// builder.Services.AddPulseBot();
    /// builder.Services.AddSingleton&lt;IAdapter, OneBotAdapter&gt;();  // 每个平台注册一个适配器
    /// builder.Services.AddPlugin&lt;EchoPlugin&gt;();                  // 插件逐个登记
    /// </code>
    /// <para>
    /// 各子域扩展（<c>AddPulseBotDispatching</c> / <c>AddPulseBotCommands</c> /
    /// <c>AddPulseBotPermissions</c> / <c>AddPulseBotPlugins</c>）都是幂等的，
    /// 单独调用其中之一也能得到可用的最小装配（权限域随命令域自动就位）。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPulseBot(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPulseBotDispatching();
        services.AddPulseBotPlugins();

        return services;
    }
}
