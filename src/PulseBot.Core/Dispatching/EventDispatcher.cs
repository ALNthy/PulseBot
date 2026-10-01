using Microsoft.Extensions.Logging;
using PulseBot.Core.Events;

namespace PulseBot.Core.Dispatching;

/// <summary>
/// <see cref="IEventDispatcher"/> 的默认实现。
/// </summary>
/// <remarks>
/// <para>
/// 处理器表与中间件管道都在构造函数中一次性建立（处理器按事件类型分组、组内按优先级降序；
/// 中间件按优先级反向包裹成一条链），此后只读。
/// 因此分发热路径上只有「一次字典查找 + 若干次委托调用」——零反射、零 <c>MethodInfo.Invoke</c>，
/// 无匹配处理器且未装中间件时以 <see cref="ValueTask.CompletedTask"/> 零分配返回。
/// </para>
/// <para>
/// 线程安全：构造完成后字典与管道不再变化，所有分发都是只读访问，可被并发调用。
/// 处理器与中间件本身必须是线程安全的（单例、可能被多事件并发进入），这一点由其作者保证。
/// </para>
/// </remarks>
public sealed class EventDispatcher : IEventDispatcher
{
    /// <summary>事件类型 → 该类型已排序的处理器委托列表。</summary>
    private readonly Dictionary<Type, IReadOnlyList<EventInvoker>> _invokersByEventType;

    /// <summary>中间件链的入口；未注册任何中间件时为 null，分发走与引入中间件前完全一致的路径。</summary>
    private readonly EventMiddlewareNext? _pipeline;

    private readonly ILogger<EventDispatcher> _logger;

    /// <summary>
    /// 创建分发器。处理器注册项与中间件均由 DI 以 <c>IEnumerable&lt;T&gt;</c> 提供。
    /// </summary>
    /// <param name="registrations">各事件类型的处理器注册项。</param>
    /// <param name="middlewares">事件中间件；为空时不建立管道。</param>
    /// <param name="logger">结构化日志。</param>
    public EventDispatcher(
        IEnumerable<IHandlerRegistration> registrations,
        IEnumerable<IEventMiddleware> middlewares,
        ILogger<EventDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(middlewares);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var map = new Dictionary<Type, IReadOnlyList<EventInvoker>>();
        foreach (IHandlerRegistration registration in registrations)
        {
            // 每种事件类型正常只有一个注册项；若出现重复，后者覆盖前者（不静默合并，避免处理器被调用两次）。
            map[registration.EventType] = registration.Invokers;
        }

        _invokersByEventType = map;
        _pipeline = BuildPipeline(middlewares);
    }

    /// <inheritdoc />
    public ValueTask DispatchAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        // typeof(TEvent) 是 JIT 常量，按泛型实参查表，不做运行时类型解析。
        return DispatchCore(typeof(TEvent), @event, cancellationToken);
    }

    /// <summary>
    /// 按事件的运行时类型分发，供 <see cref="EventPublisher"/> 这类只持有 <see cref="IEvent"/>
    /// 静态类型的入口使用。
    /// </summary>
    /// <remarks>
    /// 声明为 <c>internal</c> 且使用区别于泛型重载的方法名，是为了让调用方无法在二者之间误选：
    /// 泛型版本按编译期类型匹配，本方法按运行时类型匹配，语义不同。
    /// </remarks>
    internal ValueTask DispatchByRuntimeTypeAsync(IEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return DispatchCore(@event.GetType(), @event, cancellationToken);
    }

    /// <summary>两个入口共用的实现：查表后按「有无中间件」选择路径。</summary>
    private ValueTask DispatchCore(Type eventType, IEvent @event, CancellationToken cancellationToken)
    {
        _invokersByEventType.TryGetValue(eventType, out IReadOnlyList<EventInvoker>? invokers);

        if (_pipeline is null)
        {
            // 未装中间件：无匹配处理器时连上下文都不创建，保持零分配返回。
            return invokers is null
                ? ValueTask.CompletedTask
                : InvokeHandlersAsync(invokers, @event, new EventContext(), cancellationToken);
        }

        // 装了中间件：处理器列表随上下文传入，管道本身不捕获它（理由见 EventContext.Invokers）。
        return _pipeline(
            @event,
            new EventContext { Invokers = invokers ?? Array.Empty<EventInvoker>() },
            cancellationToken);
    }

    /// <summary>
    /// 把中间件按优先级由外到内包裹成一条链；未注册任何中间件时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 全部包装在构造期完成，分发时只剩纯委托调用。链的终端从上下文取处理器列表，
    /// 因此同一条链可以服务所有事件类型。
    /// </remarks>
    private EventMiddlewareNext? BuildPipeline(IEnumerable<IEventMiddleware> middlewares)
    {
        IEventMiddleware[] ordered = middlewares
            .OrderByDescending(static middleware => middleware.Priority)
            .ToArray();

        if (ordered.Length == 0)
        {
            return null;
        }

        // 终端：调用本次分发的处理器列表。此处捕获 this 只为把日志设施带进闭包，
        // 整个包装过程只在构造期发生一次。
        EventMiddlewareNext next = (evt, context, cancellationToken) =>
            InvokeHandlersAsync(context.Invokers, evt, context, cancellationToken);

        // 反向包裹：优先级最低的先成为内层，循环结束时 next 即优先级最高者的入口。
        for (var i = ordered.Length - 1; i >= 0; i--)
        {
            next = WrapMiddleware(ordered[i], next);
        }

        return next;
    }

    /// <summary>把单个中间件包成一层，并附上异常隔离。</summary>
    private EventMiddlewareNext WrapMiddleware(IEventMiddleware middleware, EventMiddlewareNext next)
    {
        return async (evt, context, cancellationToken) =>
        {
            try
            {
                await middleware.InvokeAsync(evt, context, next, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 宿主正在关闭：取消属于正常控制流，向上传播。
                throw;
            }
            catch (Exception ex)
            {
                // 中间件异常隔离：记录后不外传。异常点之后不会再调用 next，
                // 管道自然终止在本层——与「中间件主动短路」是同一条路径。
                _logger.LogError(
                    ex,
                    "事件 {EventType} 的中间件 {Middleware} 执行时抛出未处理异常，管道已终止。",
                    evt.GetType().Name,
                    middleware.GetType().Name);
            }
        };
    }

    /// <summary>
    /// 依次调用处理器：每轮先检查传播是否已被阻断，再调用，并对单个处理器的异常做隔离。
    /// </summary>
    /// <remarks>这是中间件管道的内层终端；未装中间件时由 <see cref="DispatchCore"/> 直接调用。</remarks>
    private async ValueTask InvokeHandlersAsync(
        IReadOnlyList<EventInvoker> invokers,
        IEvent @event,
        EventContext context,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < invokers.Count; i++)
        {
            if (context.IsPropagationStopped)
            {
                break;
            }

            try
            {
                await invokers[i](@event, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 宿主正在关闭：取消属于正常控制流，向上传播。
                throw;
            }
            catch (Exception ex)
            {
                // 处理器异常隔离：记录后继续执行其余处理器，
                // 保证单个插件的缺陷不会吞掉整条事件管道。
                _logger.LogError(
                    ex,
                    "事件 {EventType} 的处理器执行时抛出未处理异常，已隔离。",
                    @event.GetType().Name);
            }
        }
    }
}
