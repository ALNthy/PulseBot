using AnBot.Core.Events;
using AnBot.Core.Messages;

namespace AnBot.Core.Sessions;

/// <summary>
/// 交互会话的标识：<c>会话 + 用户</c> 二元组，决定「同一份会话状态归谁」。
/// </summary>
/// <remarks>
/// <para>
/// 粒度为「每个会话中的每个用户各一份」：群里每人互不干扰，私聊与群聊各自独立。
/// 用 <see cref="ConversationId"/> 而非仅用用户 id，是为了让「同一个人在私聊与不同群里」
/// 拥有彼此隔离的会话——在私聊里说过的内容不应泄漏到群里。
/// </para>
/// <para>
/// 与 <see cref="ConversationId"/> 同样选择引用类型 record：本类型要作为字典键参与查找，
/// 必须拒绝非法状态（用户 id 为空白），显式构造函数可保证该不变式。
/// </para>
/// <para>
/// 命名注意：本仓库文档里的「会话」<see cref="ConversationId"/> 指私聊/群/频道这类
/// 消息通道，与本类型的「交互会话」不是一回事，阅读时留意区分。
/// </para>
/// </remarks>
public sealed record SessionKey
{
    /// <summary>会话（平台 + 类型 + 平台内会话 id）。</summary>
    public ConversationId Conversation { get; }

    /// <summary>用户在该平台内的 id（通常取自 <see cref="MessageEvent.SenderId"/>）。</summary>
    public string UserId { get; }

    /// <summary>构造会话键；用户 id 不允许为空白。</summary>
    /// <param name="conversation">会话标识。</param>
    /// <param name="userId">用户在该平台内的 id。</param>
    public SessionKey(ConversationId conversation, string userId)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        Conversation = conversation;
        UserId = userId;
    }

    /// <summary>
    /// 从消息事件提取会话键，等价于 <c>new SessionKey(message.Conversation, message.SenderId)</c>。
    /// 这是插件最常用的入口：拿到一条消息后据此定位它所属的交互会话。
    /// </summary>
    /// <param name="message">消息事件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    public static SessionKey From(MessageEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new SessionKey(message.Conversation, message.SenderId);
    }

    /// <summary>形如 <c>onebot/Group/123456#789</c>，仅供日志与诊断，不要反向解析。</summary>
    public override string ToString() => $"{Conversation}#{UserId}";
}
