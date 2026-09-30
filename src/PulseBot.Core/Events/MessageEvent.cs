using PulseBot.Core.Adapters;
using PulseBot.Core.Messages;

namespace PulseBot.Core.Events;

/// <summary>
/// 消息事件：某个会话中收到一条消息。这是绝大多数插件唯一关心的事件类型。
/// </summary>
/// <remarks>
/// <para>
/// 「纯数据字段 + 回复能力」的组合：
/// 数据字段（会话、消息、发送者）由适配器填写；<see cref="ReplyChannel"/> 则由 Core 的事件入口
/// （<c>IEventPublisher</c>）在分发前统一装配。这样适配器既不需要、也不能依赖
/// <see cref="IMessageSender"/>——否则会与「按平台路由到适配器」的发送实现形成循环依赖。
/// </para>
/// <para>
/// 选用 record 而非 class：事件是纯数据载体，record 的值相等语义便于测试断言，
/// <c>with</c> 表达式则让「补全框架侧信息」这类加工不必引入可变状态。
/// </para>
/// </remarks>
public sealed record MessageEvent : IEvent
{
    /// <summary>事件来自哪个平台的哪个会话；同时是回复时的路由依据。</summary>
    public required ConversationId Conversation { get; init; }

    /// <summary>消息内容（平台无关的消息段序列）。</summary>
    public required Message Message { get; init; }

    /// <summary>本条消息在该平台内的 id；被动回复时据此让平台定位被引用的消息。</summary>
    public required string MessageId { get; init; }

    /// <summary>发送者在该平台内的 id。</summary>
    public required string SenderId { get; init; }

    /// <summary>发送者展示名，平台未提供时为 null。仅用于日志与提示，不参与平台协议。</summary>
    public string? SenderDisplayName { get; init; }

    /// <summary>
    /// 回复通道：由 Core 的事件入口（<c>IEventPublisher</c>）装配，适配器创建事件时不设置。
    /// 为 null 表示该事件没有连回平台的路径（例如单元测试里直接构造的数据对象）。
    /// </summary>
    public IMessageSender? ReplyChannel { get; init; }

    /// <summary>消息的纯文本呈现（图片等以占位符表示）；命令解析直接读取本属性。</summary>
    public string PlainText => Message.ToString();

    /// <summary>
    /// 回复本条消息：语义上「引用这条消息并作出回应」，适配器应优先使用平台原生的回复能力。
    /// </summary>
    /// <param name="text">回复文本。</param>
    /// <param name="cancellationToken">取消令牌，必须传播。</param>
    /// <exception cref="InvalidOperationException">
    /// 事件未装配回复通道（<see cref="ReplyChannel"/> 为 null），即它不是经由事件入口发布的。
    /// </exception>
    public async ValueTask ReplyAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (ReplyChannel is null)
        {
            throw new InvalidOperationException(
                "该消息事件没有装配回复通道，无法回复。"
                + "适配器应通过 IEventPublisher 发布事件，由 Core 统一装配回复能力。");
        }

        // 发送结果（SendResult）被有意丢弃：命令处理器的 ReplyAsync 约定返回 ValueTask，
        // 且发送失败已由 AdapterMessageSender 统一记录日志并转为失败结果；
        // 需要区分失败原因的调用方应直接注入 IMessageSender。
        await ReplyChannel.ReplyAsync(Conversation, MessageId, text, cancellationToken).ConfigureAwait(false);
    }
}
