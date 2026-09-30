using PulseBot.Core.Events;

namespace PulseBot.Core.Commands;

/// <summary>
/// 命令执行上下文：插件通过本类型拿到本次调用的全部输入，并通过它回复消息。
/// 插件只接触平台无关的抽象（消息事件 + 文本），不感知任何协议细节。
/// </summary>
public sealed class CommandContext
{
    private readonly MessageEvent _message;

    /// <summary>
    /// 创建命令上下文。由 <see cref="CommandDispatcher"/> 在命中命令后构造；
    /// 测试也可直接构造以独立调用处理器。
    /// </summary>
    /// <param name="message">触发本次命令的原始消息事件。</param>
    /// <param name="commandName">实际命中的命令名（可能是别名，保留调用者书写的大小写）。</param>
    /// <param name="argsText">命令名之后的原始参数文本（已去除首尾空白）。</param>
    /// <param name="args">按空白切分后的参数列表。</param>
    public CommandContext(MessageEvent message, string commandName, string argsText, string[] args)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(commandName);
        ArgumentNullException.ThrowIfNull(argsText);
        ArgumentNullException.ThrowIfNull(args);

        _message = message;
        CommandName = commandName;
        ArgsText = argsText;
        Args = args;
    }

    /// <summary>
    /// 触发本次命令的原始消息事件。
    /// 插件可借此读取发送者、群、原始消息段等上下文（具体成员由事件域提供）。
    /// </summary>
    public MessageEvent Message => _message;

    /// <summary>实际命中的命令名（可能是别名，保留调用者书写的大小写）。</summary>
    public string CommandName { get; }

    /// <summary>命令名之后的原始参数文本（已去除首尾空白），保留大小写与内部空白。</summary>
    public string ArgsText { get; }

    /// <summary>按空白（含全角空格）切分后的参数列表；无参数时为空数组。</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>
    /// 向消息来源回复文本，等价于 <c>Message.ReplyAsync(text, cancellationToken)</c>。
    /// </summary>
    /// <param name="text">回复文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask ReplyAsync(string text, CancellationToken cancellationToken)
        => _message.ReplyAsync(text, cancellationToken);
}
