using PulseBot.Core.Events;

namespace PulseBot.Core.Dispatching;

/// <summary>
/// 事件入口：适配器把平台事件送入 Core 的唯一通道。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IEventDispatcher"/> 分开的原因：入站与分发是两个不同的职责。
/// </para>
/// <list type="bullet">
///   <item><description>入口负责接收外部事件、补全框架侧信息（例如给消息事件装配回复通道），
///   将来还可在此加入限流、来源校验与背压。</description></item>
///   <item><description>分发只负责按优先级把事件路由到处理器。</description></item>
/// </list>
/// <para>
/// 适配器只依赖这个窄接口，分发实现的演进不会波及适配器；Core 不引用任何 Adapter 项目，
/// 而适配器引用 Core，依赖方向合法。
/// </para>
/// </remarks>
public interface IEventPublisher
{
    /// <summary>
    /// 发布一个来自适配器的事件：补齐框架侧装配后交给分发器。
    /// </summary>
    /// <param name="event">由适配器构造的事件（典型为 <see cref="MessageEvent"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示全部处理器执行完成的任务。</returns>
    ValueTask PublishAsync(IEvent @event, CancellationToken cancellationToken);
}
