using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AnBot.Core.Adapters;

namespace AnBot.Adapter.Console;

/// <summary>
/// 控制台适配器的 DI 注册扩展。
/// </summary>
public static class ConsoleAdapterServiceCollectionExtensions
{
    /// <summary>
    /// 注册控制台适配器：把标准输入/输出作为消息通道接入 AnBot。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 输入输出以 <see cref="TextReader"/> / <see cref="TextWriter"/> 的形式注册，
    /// 而不是让适配器直接调用 <c>System.Console</c>：这样测试可以用
    /// <c>StringReader</c> / <c>StringWriter</c> 替换，宿主也可把输入输出重定向到别处。
    /// 用 <c>TryAdd</c> 注册，宿主若已提供自己的实现则不会被覆盖。
    /// </para>
    /// <para>
    /// 配置绑定由宿主负责：
    /// <c>services.Configure&lt;ConsoleAdapterOptions&gt;(configuration.GetSection(ConsoleAdapterOptions.SectionName))</c>。
    /// 不绑定也不会失败——各项都有可用的默认值。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddConsoleAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ConsoleAdapterOptions>();

        // 用工厂而非实例：SetIn/SetOut 之后再解析能拿到当时的流。
        services.TryAddSingleton<TextReader>(static _ => System.Console.In);
        services.TryAddSingleton<TextWriter>(static _ => System.Console.Out);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAdapter, ConsoleAdapter>());

        return services;
    }
}
