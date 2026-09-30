using PulseBot.Core.Messages;

namespace PulseBot.Core.Adapters;

/// <summary>
/// 插件面向的发送能力（主动发送 / 被动回复）。
/// </summary>
/// <remarks>
/// 插件只依赖 Core，因此只注入本接口；实现由 Core 提供（<see cref="AdapterMessageSender"/>），
/// 它按 <see cref="ConversationId.Platform"/> 把消息路由到对应适配器的 <see cref="IAdapter.SendAsync"/>。
/// 多平台对插件完全透明；同平台多实例的路由见 <see cref="AdapterMessageSender"/> 的说明。
/// </remarks>
public interface IMessageSender
{
    /// <summary>
    /// 主动发送：把消息发到指定会话。
    /// </summary>
    /// <param name="conversation">目标会话（通常取自事件上下文）。</param>
    /// <param name="message">消息内容；可直接传字符串（隐式转换为 <see cref="Message"/>）。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    ValueTask<SendResult> SendAsync(ConversationId conversation, Message message, CancellationToken cancellationToken);

    /// <summary>
    /// 被动回复：语义上「回复某条消息」，适配器优先使用平台原生回复能力。
    /// </summary>
    /// <param name="conversation">目标会话。</param>
    /// <param name="replyToMessageId">被回复消息的平台内 id（通常取自触发事件的消息 id）。</param>
    /// <param name="message">消息内容。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    ValueTask<SendResult> ReplyAsync(ConversationId conversation, string replyToMessageId, Message message, CancellationToken cancellationToken);
}
