using AnBot.Core.Adapters;
using AnBot.Core.Events;

namespace AnBot.Core.Dispatching;

/// <summary>
/// <see cref="IEventPublisher"/> 的默认实现：为事件补齐框架侧装配，再转交分发器。
/// </summary>
public sealed class EventPublisher : IEventPublisher
{
    private readonly EventDispatcher _dispatcher;
    private readonly Func<IMessageSender> _messageSenderFactory;

    /// <summary>延迟解析出的发送器；单例内缓存，避免每条消息都走一次容器解析。</summary>
    private IMessageSender? _messageSender;

    /// <summary>
    /// 创建事件入口。
    /// </summary>
    /// <param name="dispatcher">
    /// 依赖具体类型而非 <see cref="IEventDispatcher"/>：入口需要「按运行时类型分发」这一能力，
    /// 它不在对外接口上。
    /// </param>
    /// <param name="messageSenderFactory">
    /// 发送器的工厂，用于给消息事件装配回复通道。
    /// <para>
    /// **刻意接收工厂而非 <see cref="IMessageSender"/> 本身**：适配器既生产事件（依赖本入口），
    /// 又被发送实现 <c>AdapterMessageSender</c> 依赖（它需要全部 <see cref="IAdapter"/> 建立路由表），
    /// 构造期互相解析会形成「适配器 → 事件入口 → 发送器 → 适配器」的循环依赖，容器会直接拒绝解析。
    /// 推迟到「真正要发布事件时」再解析，环即断开；此时容器已构建完毕，适配器实例也已在位。
    /// </para>
    /// </param>
    public EventPublisher(EventDispatcher dispatcher, Func<IMessageSender> messageSenderFactory)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(messageSenderFactory);

        _dispatcher = dispatcher;
        _messageSenderFactory = messageSenderFactory;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(IEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // 装配回复通道：适配器构造的事件是纯数据，回复能力由入口统一注入。
        // 这样适配器无需依赖 IMessageSender（否则会与「按平台路由回适配器」的发送实现形成循环依赖），
        // 同时保证插件拿到的 MessageEvent 一定具备回复能力。
        if (@event is MessageEvent message && message.ReplyChannel is null)
        {
            @event = message with { ReplyChannel = ResolveMessageSender() };
        }

        return _dispatcher.DispatchByRuntimeTypeAsync(@event, cancellationToken);
    }

    /// <summary>
    /// 解析发送器并缓存。并发下工厂可能被调用多次，但容器解析是幂等的且返回同一单例，
    /// 因此无须加锁——缓存只为省掉后续的容器查找。
    /// </summary>
    private IMessageSender ResolveMessageSender() => _messageSender ??= _messageSenderFactory();
}
