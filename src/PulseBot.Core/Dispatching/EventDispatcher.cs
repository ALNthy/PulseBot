using Microsoft.Extensions.Logging;
using PulseBot.Core.Events;

namespace PulseBot.Core.Dispatching;

/// <summary>
/// <see cref="IEventDispatcher"/> 的默认实现。
/// </summary>
/// <remarks>
/// <para>
/// 处理器表在构造函数中一次性建立（按事件类型分组、组内按优先级降序），此后只读。
/// 因此分发热路径上只有「一次字典查找 + 若干次委托调用」——零反射、零 <c>MethodInfo.Invoke</c>，
/// 无匹配处理器时以 <see cref="ValueTask.CompletedTask"/> 零分配返回。
/// </para>
/// <para>
/// 线程安全：构造完成后字典不再变化，所有分发都是只读访问，可被并发调用。
/// 处理器本身必须是线程安全的（单例、可能被多事件并发进入），这一点由处理器作者保证。
/// </para>
/// </remarks>
public sealed class EventDispatcher : IEventDispatcher
{
    /// <summary>事件类型 → 该类型已排序的处理器委托列表。</summary>
    private readonly Dictionary<Type, IReadOnlyList<EventInvoker>> _invokersByEventType;

    private readonly ILogger<EventDispatcher> _logger;

    /// <summary>
    /// 创建分发器。处理器注册项由 DI 以 <c>IEnumerable&lt;IHandlerRegistration&gt;</c> 提供。
    /// </summary>
    public EventDispatcher(IEnumerable<IHandlerRegistration> registrations, ILogger<EventDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var map = new Dictionary<Type, IReadOnlyList<EventInvoker>>();
        foreach (IHandlerRegistration registration in registrations)
        {
            // 每种事件类型正常只有一个注册项；若出现重复，后者覆盖前者（不静默合并，避免处理器被调用两次）。
            map[registration.EventType] = registration.Invokers;
        }

        _invokersByEventType = map;
    }

    /// <inheritdoc />
    public ValueTask DispatchAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        // typeof(TEvent) 是 JIT 常量，按泛型实参查表，不做运行时类型解析。
        return _invokersByEventType.TryGetValue(typeof(TEvent), out IReadOnlyList<EventInvoker>? invokers)
            ? InvokeAsync(invokers, @event, cancellationToken)
            : ValueTask.CompletedTask;
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

        return _invokersByEventType.TryGetValue(@event.GetType(), out IReadOnlyList<EventInvoker>? invokers)
            ? InvokeAsync(invokers, @event, cancellationToken)
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// 依次调用处理器：每轮先检查传播是否已被阻断，再调用，并对单个处理器的异常做隔离。
    /// </summary>
    private async ValueTask InvokeAsync(
        IReadOnlyList<EventInvoker> invokers,
        IEvent @event,
        CancellationToken cancellationToken)
    {
        if (invokers.Count == 0)
        {
            return;
        }

        var context = new EventContext();

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
