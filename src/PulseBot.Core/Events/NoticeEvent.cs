using PulseBot.Core.Messages;

namespace PulseBot.Core.Events;

/// <summary>通知事件的类别：把各平台叫法各异的通知归一到 Core 的语义。</summary>
public enum NoticeKind
{
    /// <summary>成员加入（群成员增加、好友增加等）。</summary>
    MemberJoined = 0,

    /// <summary>成员离开（群成员减少、好友减少等）。</summary>
    MemberLeft = 1,

    /// <summary>消息被撤回或删除。</summary>
    MessageDeleted = 2,

    /// <summary>其它通知（平台特有类型），具体类型见 <see cref="NoticeEvent.RawType"/>。</summary>
    Other = 3,
}

/// <summary>
/// 通知事件：会话中发生的、非消息的状态变化（成员进出、消息撤回等）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意做成「单类型 + 枚举」而不是「基类 + 派生」：事件分发按运行时类型精确匹配、
/// 不做基类向上匹配（见 EventDispatcher 的说明），使用继承体系只会让订阅方的处理器静默收不到事件。
/// </para>
/// <para>
/// 各字段是不同平台通知的并集，按 <see cref="Kind"/> 取用：成员进出看 <see cref="TargetUserId"/>，
/// 撤回看 <see cref="MessageId"/>；对该类通知不适用的字段为 null。
/// </para>
/// </remarks>
public sealed record NoticeEvent : IEvent
{
    /// <summary>通知发生在哪个会话。</summary>
    /// <remarks>
    /// 没有天然会话归属的通知（如好友增加）由适配器选择最贴近的会话表达，
    /// 例如「与新好友的私聊会话」，从而让权限与会话逻辑仍有一致的上下文。
    /// </remarks>
    public required ConversationId Conversation { get; init; }

    /// <summary>归一后的通知类别。</summary>
    public required NoticeKind Kind { get; init; }

    /// <summary>平台原始通知类型标识（如 OneBot 12 的 detail_type），供诊断与未映射类型兜底；适配器可留空。</summary>
    public string? RawType { get; init; }

    /// <summary>触发者（撤回者、审批人等）；平台未提供时为 null。</summary>
    public string? OperatorId { get; init; }

    /// <summary>被影响的用户（加入 / 离开的成员）。</summary>
    public string? TargetUserId { get; init; }

    /// <summary>被撤回 / 删除的消息 id。</summary>
    public string? MessageId { get; init; }
}
