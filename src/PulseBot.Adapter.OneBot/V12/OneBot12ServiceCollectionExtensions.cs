using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseBot.Core.Adapters;

namespace PulseBot.Adapter.OneBot.V12;

/// <summary>OneBot 12 适配器的依赖注入注册。</summary>
public static class OneBot12ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 OneBot 12 适配器。
    /// </summary>
    /// <remarks>
    /// 只注册 <see cref="IAdapter"/> 实现并开启配置绑定；配置值由宿主通过
    /// <c>Configure&lt;OneBot12Options&gt;</c> 或 appsettings.json 的
    /// <see cref="OneBot12Options.SectionName"/> 节提供。
    /// </remarks>
    public static IServiceCollection AddOneBot12Adapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OneBot12Options>();

        // TryAddEnumerable 按实现类型去重，宿主重复调用本方法不会注册出两条适配器。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAdapter, OneBot12Adapter>());

        return services;
    }
}
