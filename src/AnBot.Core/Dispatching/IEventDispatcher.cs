using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// 事件分发器：把事件按运行时类型路由给已注册的处理器。
/// </summary>
public interface IEventDispatcher
{
    /// <summary>
    /// 分发一个事件：按优先级从高到低依次调用所有匹配的处理器。
    /// </summary>
    /// <typeparam name="TEvent">事件的具体类型。</typeparam>
    /// <param name="event">事件实例。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示全部处理器执行完成的任务。</returns>
    /// <remarks>
    /// <para>
    /// 处理器可通过 <see cref="EventContext.StopPropagation"/> 阻断后续处理器；
    /// 单个处理器抛出的异常被捕获并记录，不影响其它处理器。
    /// </para>
    /// <para>
    /// 采用泛型方法而非接收 <see cref="IEvent"/>：调用方在编译期就确定了事件类型，
    /// 分发器得以直接按 <c>typeof(TEvent)</c> 查表，不需要运行时类型解析。
    /// 需要按运行时类型分发的场景（适配器入口）请使用 <c>IEventPublisher</c>。
    /// </para>
    /// </remarks>
    ValueTask DispatchAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : IEvent;
}
