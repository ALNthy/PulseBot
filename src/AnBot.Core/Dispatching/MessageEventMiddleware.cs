using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// 只关心 <see cref="MessageEvent"/> 的中间件基类：其它事件类型一律直接放行。
/// </summary>
/// <remarks>
/// 大多数中间件（限流、审计、文本预处理）只对聊天消息有意义，把类型判断收拢在这里，
/// 子类就不必在每个中间件里重复写一遍。若中间件需要观察**所有**事件类型，直接实现
/// <see cref="IEventMiddleware"/>。
/// </remarks>
public abstract class MessageEventMiddleware : IEventMiddleware
{
    /// <inheritdoc />
    public virtual int Priority => 0;

    /// <inheritdoc />
    public ValueTask InvokeAsync(
        IEvent @event,
        EventContext context,
        EventMiddlewareNext next,
        CancellationToken cancellationToken)
        => @event is MessageEvent message
            ? InvokeAsync(message, context, next, cancellationToken)
            : next(@event, context, cancellationToken);

    /// <summary>
    /// 处理消息事件。不调用 <paramref name="next"/> 即终止管道。
    /// </summary>
    /// <param name="event">消息事件。</param>
    /// <param name="context">本次分发的上下文。</param>
    /// <param name="next">续延；不关心这条消息时调用它放行。</param>
    /// <param name="cancellationToken">取消令牌（宿主关闭时触发），必须向下传播。</param>
    /// <returns>表示处理完成的任务。</returns>
    public abstract ValueTask InvokeAsync(
        MessageEvent @event,
        EventContext context,
        EventMiddlewareNext next,
        CancellationToken cancellationToken);
}
