using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseBot.Core.Adapters;

namespace PulseBot.Adapter.Email;

/// <summary>邮件适配器的依赖注入注册。</summary>
public static class EmailAdapterServiceCollectionExtensions
{
    /// <summary>
    /// 注册邮件适配器。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只注册 <see cref="IAdapter"/> 实现并开启配置绑定；是否真正连接邮箱由
    /// <c>Adapters:Email:Enabled</c> 决定（默认 false）。这样默认宿主可以安全地注册它，
    /// 不需要邮箱的场景不会产生任何网络连接。
    /// </para>
    /// <para>
    /// 配置由宿主通过 <c>Configure&lt;EmailAdapterOptions&gt;</c> 或 appsettings.json 的
    /// <see cref="EmailAdapterOptions.SectionName"/> 节提供。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddEmailAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<EmailAdapterOptions>();

        // TryAddEnumerable 按实现类型去重，宿主重复调用本方法不会注册出两条适配器。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAdapter, EmailAdapter>());

        return services;
    }
}
