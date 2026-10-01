namespace PulseBot.Core.Adapters;

/// <summary>
/// 出站中间件的续延：调用它才会继续走到下一个中间件，最终把消息交给适配器发送。
/// </summary>
/// <remarks>
/// <para>
/// 中间件可以在调用前做预处理、在调用后处理发送结果，也可以把 <paramref name="message"/>
/// 换成改写后的新实例再传下去——更内层的中间件与适配器收到的就是新实例。
/// <see cref="OutboundMessage"/> 是记录类，改写用 <c>with</c> 表达式即可。
/// </para>
/// <para>
/// 一次发送中只应调用它一次：管道的语义是「一条链走到头」，重复调用会把同一条消息发给适配器多遍。
/// </para>
/// </remarks>
/// <param name="message">要交给下游的出站消息；可传入改写后的新实例。</param>
/// <param name="cancellationToken">取消令牌，必须向下传播。</param>
public delegate ValueTask<SendResult> OutboundMiddlewareNext(
    OutboundMessage message,
    CancellationToken cancellationToken);

/// <summary>
/// 出站中间件契约：包裹「把消息交给适配器」这一步，可做前处理、改写消息、阻断发送与后处理。
/// </summary>
/// <remarks>
/// <para>
/// 管道形状与 <see cref="Dispatching.IEventMiddleware"/> 一致：洋葱式，<see cref="Priority"/>
/// 大者在外层（前处理最先执行、后处理最后执行），同优先级按 DI 注册顺序进入。
/// 但本接口没有 <c>EventContext</c> 那样的上下文参数——出站链路不存在需要跨层共享的可变状态：
/// 改写靠续延的参数传递，发送结果由返回值携带，日志等设施由各中间件自行注入，
/// 多出来的上下文只会是纯粹的预留。
/// </para>
/// <para>
/// <b>默认路径必须是 <c>return await next(...)</c>。</b>不调用续延即意味着「这条消息不再发送」，
/// 此时必须自行构造并返回失败结果（限流与拦截建议用 <see cref="SendFailureKind.Blocked"/>，
/// 语义是「框架主动拦截」而非「平台拒绝」），否则调用方会拿到一个无来源的默认结果。
/// 与事件侧一样，忘记调用续延是最难排查的故障形态（消息静默消失，没有任何报错）。
/// </para>
/// <para>
/// 中间件由 DI 构造（构造函数注入可用）且必须是单例：发送器在容器构建阶段就把它们绑定成委托，
/// 之后不再从容器解析。中间件本身必须是线程安全的——同一条链会被并发发送共用。
/// </para>
/// <para>
/// 中间件抛出的异常由发送器统一捕获并记录，随后返回 <see cref="SendFailureKind.Unknown"/> 失败结果：
/// 消息<b>不会</b>发出（fail-closed）。这与适配器异常的既有处理一致——出站是外部边界，
/// 异常不得穿透到插件与命令的热路径；对限流、脱敏这类中间件来说，故障时宁可少发也不可漏发。
/// 因此中间件若要「失败也放行」，必须自己 try/catch。
/// 唯一例外是调用方自身的取消（令牌已触发）会正常向上传播。
/// </para>
/// <para>
/// 与 <see cref="Dispatching.IEventMiddleware"/> 的分工：两者都是横切关注点的挂载点，
/// 但事件侧面向「收到了什么」，出站侧面向「要发出去什么」。
/// 出站限流防刷、用户与群黑名单、敏感词过滤、统一前缀、发送审计与统计都装在这一层。
/// </para>
/// </remarks>
public interface IOutboundMiddleware
{
    /// <summary>
    /// 优先级：数值大者在外层（前处理最早、后处理最晚），默认为 0。
    /// </summary>
    int Priority => 0;

    /// <summary>
    /// 处理一次出站发送。不调用 <paramref name="next"/> 即阻断发送。
    /// </summary>
    /// <param name="message">出站消息；改写后用 <paramref name="next"/> 传入新实例。</param>
    /// <param name="next">续延；调用它才会继续走到下一个中间件与适配器。</param>
    /// <param name="cancellationToken">取消令牌，必须向下传播。</param>
    /// <returns>本次发送的结果；阻断发送时返回自行构造的失败结果。</returns>
    ValueTask<SendResult> InvokeAsync(
        OutboundMessage message,
        OutboundMiddlewareNext next,
        CancellationToken cancellationToken);
}
