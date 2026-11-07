using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// 非泛型的事件调用委托：分发器内部以它统一缓存与调用所有处理器，
/// 使分发热路径上既没有反射，也没有 <c>MethodInfo.Invoke</c>。
/// </summary>
/// <param name="event">事件实例。</param>
/// <param name="context">分发上下文。</param>
/// <param name="cancellationToken">取消令牌。</param>
public delegate ValueTask EventInvoker(IEvent @event, EventContext context, CancellationToken cancellationToken);

/// <summary>
/// 处理器注册项：把某一事件类型的全部处理器收拢为一组已排序的非泛型委托。
/// </summary>
/// <remarks>
/// 这是「泛型处理器接口」与「非泛型分发器」之间的适配层：由于 <c>IEventHandler&lt;TEvent&gt;</c>
/// 无法协变（它同时消费事件），分发器无法直接把不同事件类型的处理器放进同一个集合，
/// 因此由本注册项在启动阶段把每个处理器包装成 <see cref="EventInvoker"/> 并缓存。
/// </remarks>
public interface IHandlerRegistration
{
    /// <summary>本注册项负责的事件类型。</summary>
    Type EventType { get; }

    /// <summary>该事件类型的全部处理器调用委托，已按优先级降序排序。</summary>
    IReadOnlyList<EventInvoker> Invokers { get; }
}

/// <summary>
/// <see cref="IHandlerRegistration"/> 的默认实现：在容器构建阶段把
/// <see cref="IEventHandler{TEvent}"/> 适配为 <see cref="EventInvoker"/> 并按优先级排序。
/// </summary>
/// <remarks>
/// 由 <c>AddEventHandler&lt;TEvent, THandler&gt;()</c> 按事件类型注册一次；
/// 它通过构造函数注入该类型的全部处理器，因此后续再向同一事件类型追加处理器时无需重复注册本项。
/// </remarks>
/// <typeparam name="TEvent">事件类型。</typeparam>
public sealed class HandlerRegistration<TEvent> : IHandlerRegistration where TEvent : IEvent
{
    /// <summary>
    /// 创建注册项并完成「排序 + 委托绑定」。
    /// </summary>
    /// <param name="handlers">该事件类型的全部处理器（由 DI 以 <c>IEnumerable&lt;IEventHandler&lt;TEvent&gt;&gt;</c> 提供）。</param>
    public HandlerRegistration(IEnumerable<IEventHandler<TEvent>> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        // 先按优先级降序排序（OrderByDescending 是稳定排序，同优先级保持注册顺序），
        // 再把每个强类型处理器投影为统一的非泛型委托。
        // 闭包里的强制转换是编译期确定的引用转换，不是反射，因此热路径零反射开销。
        Invokers = handlers
            .OrderByDescending(static handler => handler.Priority)
            .Select(static handler => (EventInvoker)((evt, context, cancellationToken) =>
                handler.HandleAsync((TEvent)evt, context, cancellationToken)))
            .ToArray();
    }

    /// <inheritdoc />
    public Type EventType => typeof(TEvent);

    /// <inheritdoc />
    public IReadOnlyList<EventInvoker> Invokers { get; }
}
