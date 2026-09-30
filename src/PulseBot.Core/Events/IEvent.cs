namespace PulseBot.Core.Events;

/// <summary>
/// 所有事件的根契约：任何会被分发的事件都必须实现本接口。
/// </summary>
/// <remarks>
/// <para>
/// 这是一个刻意的空标记接口，不带任何成员：
/// </para>
/// <list type="number">
///   <item><description>事件是纯数据载体，各自需要的字段差异极大；强行统一（例如要求
///   Id / 时间戳）只会迫使无关事件携带无意义字段，违背「简单优先」。</description></item>
///   <item><description>适配器负责把平台原始报文转换为具体事件类型（典型为
///   <see cref="MessageEvent"/>），插件只处理事件、不接触平台协议。</description></item>
///   <item><description>若将来确需统一元数据（发生时间、来源适配器等），应经由分发上下文
///   <see cref="EventContext"/> 传递，而不是污染每一个事件类型。</description></item>
/// </list>
/// </remarks>
public interface IEvent
{
}
