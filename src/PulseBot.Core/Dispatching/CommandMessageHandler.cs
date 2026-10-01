using PulseBot.Core.Commands;
using PulseBot.Core.Events;

namespace PulseBot.Core.Dispatching;

/// <summary>
/// 内建处理器：把消息事件接入命令分发器，是「事件管道」与「命令」之间唯一的桥。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>AddPulseBotDispatching()</c> 自动注册，插件与宿主都无需手动登记。
/// </para>
/// <para>
/// 优先级取 <c>int.MaxValue - 2</c>，是内建三档里最低的一档，必须**先于**普通消息监听器
/// （默认优先级 0）处理，但**后于**接入控制（<c>AccessControlHandler</c> 取
/// <see cref="int.MaxValue"/>）与会话拦截（<c>SessionPromptHandler</c> 取 <c>int.MaxValue - 1</c>）：
/// 被拉黑的人不该有机会触发命令，正在等待回答的用户所说的话也不该被当成命令抢走。
/// 三档取值互不相同，因此执行顺序只由数值决定，与注册顺序无关。
/// </para>
/// <para>
/// 命中后调用 <see cref="EventContext.StopPropagation"/> 阻断传播，
/// 避免已作为命令被消费的消息又被监听器当作普通聊天处理一遍。
/// </para>
/// <para>
/// 本类型是公开的，因为 DI 容器需要以反射方式实例化它（非公开类型无法构造）；
/// 但它属于框架内部机制，宿主既不需要注册它，也不应直接依赖它。
/// </para>
/// </remarks>
public sealed class CommandMessageHandler : IEventHandler<MessageEvent>
{
    private readonly ICommandDispatcher _commandDispatcher;

    /// <summary>构造函数注入命令分发器。</summary>
    /// <param name="commandDispatcher">命令分发器（由命令域注册）。</param>
    public CommandMessageHandler(ICommandDispatcher commandDispatcher)
    {
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        _commandDispatcher = commandDispatcher;
    }

    /// <inheritdoc />
    public int Priority => int.MaxValue - 2;

    /// <inheritdoc />
    public async ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        // 命令分发器内部已对插件异常做了隔离，此处不会因插件缺陷抛出；
        // 唯一可能向上传播的是宿主关闭时的取消信号，交由分发器统一处理。
        if (await _commandDispatcher.TryDispatchAsync(@event, cancellationToken).ConfigureAwait(false))
        {
            context.StopPropagation();
        }
    }
}
