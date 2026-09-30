namespace PulseBot.Core.Events;

/// <summary>
/// 单次事件分发的上下文：在同一次分发内被所有处理器共享，
/// 是处理器之间传递「本次分发状态」的通道。
/// </summary>
/// <remarks>
/// 每次 <c>EventDispatcher.DispatchAsync</c> 都会创建新实例，因此不存在跨事件的串扰；
/// 同时它不跨线程共享——同一事件的处理链是严格串行的（按优先级依次 await），
/// 无需任何同步开销。
/// </remarks>
public sealed class EventContext
{
    /// <summary>是否已请求终止传播。</summary>
    public bool IsPropagationStopped { get; private set; }

    /// <summary>
    /// 请求终止后续处理器的执行。
    /// </summary>
    /// <remarks>
    /// 语义要点：
    /// <list type="bullet">
    ///   <item><description>当前处理器会正常执行完毕——本方法只是立一个标记，不会中断自身。</description></item>
    ///   <item><description>其后优先级更低、以及同优先级但排在后面的处理器都不再被调用。</description></item>
    ///   <item><description>典型用途是命令分发：命中命令的消息已被消费，不应再流入普通消息监听器。</description></item>
    /// </list>
    /// </remarks>
    public void StopPropagation()
    {
        IsPropagationStopped = true;
    }
}
