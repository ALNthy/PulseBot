using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AnBot.Core.Adapters;

/// <summary>
/// 出站域的 DI 注册扩展。
/// </summary>
public static class OutboundServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个出站中间件。
    /// </summary>
    /// <remarks>
    /// 中间件包裹「把消息交给适配器」这一步，按 <see cref="IOutboundMiddleware.Priority"/> 由外到内排序，
    /// 可以前处理、改写消息、阻断发送，也可以后处理发送结果。宿主与插件都可以注册自己的中间件。
    /// 同一中间件重复注册会被自动去重。
    /// </remarks>
    /// <typeparam name="TMiddleware">中间件实现类型，由 DI 构造（构造函数注入可用）。</typeparam>
    public static IServiceCollection AddOutboundMiddleware<TMiddleware>(this IServiceCollection services)
        where TMiddleware : class, IOutboundMiddleware
    {
        ArgumentNullException.ThrowIfNull(services);

        // 与 AddEventMiddleware 同样是 TryAddEnumerable：重复调用不会让中间件执行两次。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutboundMiddleware, TMiddleware>());

        return services;
    }
}
