using AnBot.Core.Events;

namespace AnBot.Core.Commands;

/// <summary>
/// 命令分发器：把一条消息事件解析为命令并调用对应处理器。
/// </summary>
/// <remarks>
/// 接入方式：事件管道收到 <see cref="MessageEvent"/> 后调用 <see cref="TryDispatchAsync"/>，
/// 返回 <c>true</c> 表示该消息已被命令消费，调用方（事件分发/中间件）可据此决定
/// 是否 <c>StopPropagation()</c>，避免其它插件重复处理同一条命令消息。
/// </remarks>
public interface ICommandDispatcher
{
    /// <summary>
    /// 当前命令表的只读快照（触发名、声明主名、所属插件、说明与优先级）。
    /// 每个可触发的命令名（含别名）一条记录；仅供诊断与后台展示，不要在热路径上调用。
    /// </summary>
    IReadOnlyCollection<CommandDescriptor> Commands { get; }

    /// <summary>
    /// 尝试把消息事件作为命令分发。
    /// </summary>
    /// <param name="messageEvent">待解析的消息事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中并执行了命令返回 <c>true</c>；不是命令或未命中返回 <c>false</c>。</returns>
    /// <remarks>
    /// 处理器内部抛出的异常由本方法捕获并记录（插件异常隔离），不会向调用方传播；
    /// 唯一例外是宿主关闭（<paramref name="cancellationToken"/> 已取消）时的取消信号会正常向上传播。
    /// </remarks>
    ValueTask<bool> TryDispatchAsync(MessageEvent messageEvent, CancellationToken cancellationToken);
}
