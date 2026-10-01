using PulseBot.Core.Dispatching;

namespace PulseBot.Core.Events;

/// <summary>
/// 单次事件分发的上下文：在同一次分发内被所有中间件与处理器共享，
/// 是它们之间传递「本次分发状态」的通道。
/// </summary>
/// <remarks>
/// 每次分发都会创建新实例，因此不存在跨事件的串扰；
/// 同时它不跨线程共享——同一事件的处理链是严格串行的（按优先级依次 await），
/// 无需任何同步开销。
/// </remarks>
public sealed class EventContext
{
    /// <summary>是否已请求终止后续处理器的执行。</summary>
    public bool IsPropagationStopped { get; private set; }

    /// <summary>
    /// 本次分发要调用的处理器委托列表；由分发器填充。
    /// </summary>
    /// <remarks>
    /// 放在上下文里，是为了让「该调哪些处理器」这条与事件类型相关的数据随每次分发传入——
    /// 中间件管道因此只需在构造期构建一条，不必为每种事件类型各存一条，也不必在热路径上分配闭包。
    /// 它是分发器的内部机制，中间件与处理器都不应读写。
    /// </remarks>
    internal IReadOnlyList<EventInvoker> Invokers { get; init; } = Array.Empty<EventInvoker>();

    /// <summary>
    /// 请求终止后续处理器的执行。
    /// </summary>
    /// <remarks>
    /// 语义要点：
    /// <list type="bullet">
    ///   <item><description>当前处理器会正常执行完毕——本方法只是立一个标记，不会中断自身。</description></item>
    ///   <item><description>其后优先级更低、以及同优先级但排在后面的处理器都不再被调用。</description></item>
    ///   <item><description>典型用途是命令分发之后的监听器：命中命令的消息已被消费，不应再流入普通消息监听器。</description></item>
    ///   <item><description>本方法只影响处理器之间；中间件要终止整条管道应当不调用续延（<c>next</c>）。</description></item>
    /// </list>
    /// </remarks>
    public void StopPropagation()
    {
        IsPropagationStopped = true;
    }
}
