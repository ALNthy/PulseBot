using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// 事件处理器契约：关心某一类事件的组件实现本接口，由 <c>EventDispatcher</c> 按优先级调用。
/// </summary>
/// <remarks>
/// <para>
/// 匹配是「运行时类型精确相等」，不做基类/接口向上匹配：注册 <c>IEventHandler&lt;MessageEvent&gt;</c>
/// 只会收到 <see cref="MessageEvent"/>，不会收到其它事件。这样行为可预测，也避免了按类型层级
/// 展开的复杂度。
/// </para>
/// <para>
/// 处理器由 DI 构造（构造函数注入可用），且必须是单例：分发器在容器构建阶段就把它们绑定成委托，
/// 之后不再从容器解析。
/// </para>
/// </remarks>
/// <typeparam name="TEvent">关心的事件类型。</typeparam>
public interface IEventHandler<TEvent> where TEvent : IEvent
{
    /// <summary>
    /// 优先级：数值大者先执行，默认为 0。
    /// </summary>
    /// <remarks>
    /// 同优先级的处理器按注册顺序执行。框架内建的三个机制（接入控制、会话拦截、命令分发）
    /// 不在这里参与排序——它们位于处理器列表之外的中间件层，见 <see cref="IEventMiddleware"/>。
    /// 处理器列表里只有业务监听器。
    /// </remarks>
    int Priority => 0;

    /// <summary>
    /// 处理事件。
    /// </summary>
    /// <param name="event">事件实例；其运行时类型保证为 <typeparamref name="TEvent"/>。</param>
    /// <param name="context">本次分发的上下文，可调用 <see cref="EventContext.StopPropagation"/> 阻断后续处理器。</param>
    /// <param name="cancellationToken">取消令牌（宿主关闭时触发），必须向下传播。</param>
    /// <returns>表示处理完成的任务。</returns>
    /// <remarks>
    /// 处理器内部抛出的异常由分发器统一捕获并记录（插件异常隔离），不会中断同批次其它处理器，
    /// 也不会导致宿主崩溃；唯一例外是宿主关闭时的取消信号会正常向上传播。
    /// </remarks>
    ValueTask HandleAsync(TEvent @event, EventContext context, CancellationToken cancellationToken);
}
