using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AnBot.Core.Adapters;
using AnBot.Core.Commands;
using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// 事件分发域的 DI 注册扩展。
/// </summary>
public static class DispatchingServiceCollectionExtensions
{
    /// <summary>
    /// 注册事件分发服务：分发器、事件入口、默认发送实现，以及「消息 → 命令」的内建桥接中间件。
    /// </summary>
    /// <remarks>
    /// 命令分发器是事件管道的下游消费者，因此这里顺带确保命令服务在位
    /// （<see cref="CommandServiceCollectionExtensions.AddAnBotCommands"/> 是幂等的）。
    /// 发送实现同理：事件入口需要它来给消息事件装配回复通道，
    /// 用 <c>TryAdd</c> 注册，宿主可在之后覆盖为自己的实现。
    /// </remarks>
    public static IServiceCollection AddAnBotDispatching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAnBotCommands();

        // 默认发送实现：按平台路由到已注册的适配器；没有适配器时也能构造（只是发送一律失败）。
        services.TryAddSingleton<IMessageSender, AdapterMessageSender>();

        services.TryAddSingleton<EventDispatcher>();
        services.TryAddSingleton<IEventDispatcher>(static provider => provider.GetRequiredService<EventDispatcher>());

        // 事件入口用工厂注册：发送器在「首次发布事件」时才解析，而不是构造期。
        // 适配器既依赖本入口（发布事件）又被发送实现依赖（建立平台路由表），
        // 构造期互相解析会构成循环依赖——详见 EventPublisher 构造函数的说明。
        services.TryAddSingleton<IEventPublisher>(static provider => new EventPublisher(
            provider.GetRequiredService<EventDispatcher>(),
            () => provider.GetRequiredService<IMessageSender>()));

        // 内建桥接：命中的命令消息会被消费，管道终止，不再流向下游监听器。
        services.AddEventMiddleware<CommandMiddleware>();

        return services;
    }

    /// <summary>
    /// 注册一个事件处理器。
    /// </summary>
    /// <remarks>
    /// 同一处理器重复注册会被自动去重；对同一事件类型多次调用本方法追加不同处理器是安全的，
    /// 该事件类型的「注册项」只会建立一次。
    /// </remarks>
    /// <typeparam name="TEvent">处理器关心的事件类型。</typeparam>
    /// <typeparam name="THandler">处理器实现类型，由 DI 构造（构造函数注入可用）。</typeparam>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IEvent
        where THandler : class, IEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        // TryAddEnumerable 按「服务类型 + 实现类型」去重，重复调用不会让处理器被执行两次。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventHandler<TEvent>, THandler>());

        // 每种事件类型只需一个注册项：它会把该类型的全部处理器一次性收拢并排序。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHandlerRegistration, HandlerRegistration<TEvent>>());

        return services;
    }

    /// <summary>
    /// 注册一个事件中间件。
    /// </summary>
    /// <remarks>
    /// 中间件包裹整条分发链，按 <see cref="IEventMiddleware.Priority"/> 由外到内排序，
    /// 可以前处理、改写事件、后处理，也可以不调用续延来终止管道。宿主与插件都可以注册自己的中间件。
    /// 同一中间件重复注册会被自动去重。
    /// </remarks>
    /// <typeparam name="TMiddleware">中间件实现类型，由 DI 构造（构造函数注入可用）。</typeparam>
    public static IServiceCollection AddEventMiddleware<TMiddleware>(this IServiceCollection services)
        where TMiddleware : class, IEventMiddleware
    {
        ArgumentNullException.ThrowIfNull(services);

        // 与 AddEventHandler 同样是 TryAddEnumerable：重复调用不会让中间件执行两次。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventMiddleware, TMiddleware>());

        return services;
    }
}
