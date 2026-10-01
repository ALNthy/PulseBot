using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseBot.Core.Commands;

namespace PulseBot.Core.Plugins;

/// <summary>
/// 插件域的 DI 注册扩展。
/// </summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// 注册插件系统：<see cref="PluginManager"/> 单例，并确保命令分发服务已注册。
    /// 宿主仍需用 <see cref="AddPlugin{TPlugin}"/> 显式登记具体插件（编译期引用，不做运行时 DLL 扫描）。
    /// </summary>
    public static IServiceCollection AddPulseBotPlugins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPulseBotCommands();

        // 插件启用状态：命令分发与插件管理器共用同一份，后台管理通过它驱动启停。
        services.TryAddSingleton<PluginStateRegistry>();

        services.TryAddSingleton<PluginManager>();
        return services;
    }

    /// <summary>
    /// 注册一个插件：以 <see cref="IPlugin"/> 单例的形式注册（构造注入可用），
    /// 并由 <see cref="PluginManager"/> 统一加载。重复注册同一插件类型会被去重。
    /// </summary>
    /// <remarks>
    /// 插件实例只以 <see cref="IPlugin"/> 接口对外可见，不额外注册 <c>TPlugin</c> 自身服务——
    /// 双注册会创建两个插件实例，破坏「插件是容器单例」的前提；
    /// 插件间通信应通过事件总线或自有服务接口，而不是解析其它插件类型。
    /// </remarks>
    /// <typeparam name="TPlugin">插件实现类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="configurePluginServices">
    /// 插件自有服务的注册回调（插件项目推荐封装为自己的扩展方法，例如
    /// <c>services.AddPlugin&lt;EchoPlugin&gt;(s =&gt; s.AddSingleton&lt;IEchoStore, EchoStore&gt;())</c>）。
    /// </param>
    public static IServiceCollection AddPlugin<TPlugin>(
        this IServiceCollection services,
        Action<IServiceCollection>? configurePluginServices = null)
        where TPlugin : class, IPlugin
    {
        ArgumentNullException.ThrowIfNull(services);

        // 插件自有服务先注册，供插件构造函数注入。
        configurePluginServices?.Invoke(services);

        // 以实现类型形式注册：TryAddEnumerable 按 (IPlugin, TPlugin) 去重，
        // 重复注册不会导致插件被加载两次。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlugin, TPlugin>());
        return services;
    }

    /// <summary>
    /// 注册一个插件生命周期中间件。
    /// </summary>
    /// <remarks>
    /// 中间件逐插件包裹加载与卸载两个阶段，按 <see cref="IPluginLifecycleMiddleware.Priority"/> 由外到内排序，
    /// 可以前处理、后处理、跳过插件，也可以用 <c>try/catch</c> 包住续延做失败补偿。
    /// 宿主与插件都可以注册自己的中间件。同一中间件重复注册会被自动去重。
    /// </remarks>
    /// <typeparam name="TMiddleware">中间件实现类型，由 DI 构造（构造函数注入可用）。</typeparam>
    public static IServiceCollection AddPluginLifecycleMiddleware<TMiddleware>(this IServiceCollection services)
        where TMiddleware : class, IPluginLifecycleMiddleware
    {
        ArgumentNullException.ThrowIfNull(services);

        // 与 AddEventMiddleware 同样是 TryAddEnumerable：重复调用不会让中间件执行两次。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPluginLifecycleMiddleware, TMiddleware>());

        return services;
    }
}
