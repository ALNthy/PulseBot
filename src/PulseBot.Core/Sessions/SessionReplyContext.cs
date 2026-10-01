using PulseBot.Core.Events;

namespace PulseBot.Core.Sessions;

/// <summary>
/// 一轮问答中「用户回答」的上下文：回调据此读取用户说了什么，并向用户回复。
/// </summary>
/// <remarks>
/// 构造函数公开，便于测试直接构造以独立调用回调，与 <c>CommandContext</c> 的处理一致。
/// </remarks>
public sealed class SessionReplyContext
{
    private readonly MessageEvent _message;

    /// <summary>创建回答上下文。由框架在截获回答时构造。</summary>
    /// <param name="message">用户发来的那条消息事件。</param>
    /// <param name="session">该回答所属的交互会话。</param>
    public SessionReplyContext(MessageEvent message, Session session)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(session);

        _message = message;
        Session = session;
    }

    /// <summary>
    /// 用户发来的那条消息事件：原文、消息段、发送者、所属会话都在这里。
    /// </summary>
    public MessageEvent Message => _message;

    /// <summary>回答所属的交互会话；回调可在此继续提问或读写状态。</summary>
    public Session Session { get; }

    /// <summary>
    /// 向用户回复文本，等价于 <c>Message.ReplyAsync(text, cancellationToken)</c>。
    /// </summary>
    /// <param name="text">回复文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask ReplyAsync(string text, CancellationToken cancellationToken)
        => _message.ReplyAsync(text, cancellationToken);
}
