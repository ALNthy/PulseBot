using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// 事件中间件的续延：调用它才会继续走到下一个中间件，最终抵达处理器列表。
/// </summary>
/// <remarks>
/// <para>
/// 中间件可以在调用前做预处理、在调用后做后处理，也可以把 <paramref name="event"/> 换成改写后的
/// 新实例再传下去——更内层的中间件与处理器收到的就是新实例。
/// </para>
/// <para>
/// 一次分发中只应调用它一次：管道的语义是「一条链走到头」，重复调用会让处理器被执行多遍。
/// </para>
/// </remarks>
/// <param name="event">要交给下游处理的事件；可传入改写后的新实例，但不应改变其运行时类型。</param>
/// <param name="context">本次分发的上下文，全程共享同一个实例。</param>
/// <param name="cancellationToken">取消令牌，必须向下传播。</param>
public delegate ValueTask EventMiddlewareNext(
    IEvent @event,
    EventContext context,
    CancellationToken cancellationToken);

/// <summary>
/// 事件中间件契约：包裹整条事件分发链，可做前处理、改写事件、后处理与短路。
/// </summary>
/// <remarks>
/// <para>
/// 管道形状是「洋葱」：<see cref="Priority"/> 大者在外层，其前处理最先执行、后处理最后执行。
/// 中间件由 DI 构造（构造函数注入可用）且必须是单例：分发器在容器构建阶段就把它们绑定成委托，
/// 之后不再从容器解析。
/// </para>
/// <para>
/// <b>默认路径必须是 <c>await next(...)</c>。</b>不调用续延即意味着「这条事件到此为止」，
/// 后续中间件与全部处理器都不会执行——这是短路的唯一手段，也是忘记调用时最难排查的故障
/// （消息静默消失，没有任何报错）。
/// </para>
/// <para>
/// 与 <see cref="IEventHandler{TEvent}"/> 的分工：中间件包裹整条链，适合限流、审计、来源校验这类
/// 横切关注点，能改写事件、能做后处理；处理器是链的终点，适合「对事件本身做点什么」的业务逻辑，
/// 可通过 <see cref="EventContext.StopPropagation"/> 阻断同批次后续处理器。
/// </para>
/// <para>
/// 中间件会收到**所有**事件类型（接口不做泛型匹配）。只关心某一类事件的中间件请自行判断类型，
/// 或直接继承 <see cref="MessageEventMiddleware"/>。
/// </para>
/// <para>
/// Core 内建的三个中间件占据 <see cref="int.MaxValue"/> 起的三档且互不相同，依次为：接入控制
/// （<c>AccessControlMiddleware</c>）、会话拦截（<c>SessionPromptMiddleware</c>）、
/// 命令分发（<c>CommandMiddleware</c>）。因此它们的执行顺序只由数值决定，与 DI 注册顺序无关；
/// 同优先级的中间件按注册顺序进入——宿主注册的中间件可能与内建中间件落进同一档
/// （例如后台的事件镜像与会话拦截同为 <c>int.MaxValue - 1</c>）。
/// <b>自建中间件请避开这三档</b>，否则会打乱「先拦截 → 再截获回答 → 再匹配命令」的次序。
/// </para>
/// <para>
/// 中间件内部抛出的异常由分发器统一捕获并记录，不会向上传播、也不会导致宿主崩溃；
/// 异常发生时管道自然终止（异常点之后的续延不会被调用），与短路是同一条路径。
/// 因此中间件若要「失败也放行」，必须自己 try/catch。唯一例外是宿主关闭时的取消信号会正常向上传播。
/// </para>
/// </remarks>
public interface IEventMiddleware
{
    /// <summary>
    /// 优先级：数值大者在外层（前处理最早、后处理最晚），默认为 0。
    /// </summary>
    int Priority => 0;

    /// <summary>
    /// 处理事件。不调用 <paramref name="next"/> 即终止管道。
    /// </summary>
    /// <param name="event">事件实例；运行时类型可能是任意 <see cref="IEvent"/> 实现。</param>
    /// <param name="context">本次分发的上下文，全程共享同一个实例。</param>
    /// <param name="next">续延；调用它才会继续走到下一个中间件与处理器。</param>
    /// <param name="cancellationToken">取消令牌（宿主关闭时触发），必须向下传播。</param>
    /// <returns>表示处理完成的任务。</returns>
    ValueTask InvokeAsync(
        IEvent @event,
        EventContext context,
        EventMiddlewareNext next,
        CancellationToken cancellationToken);
}
