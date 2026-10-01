namespace PulseBot.Core.Events;

/// <summary>请求事件的类别。</summary>
public enum RequestKind
{
    /// <summary>加好友请求。</summary>
    Friend = 0,

    /// <summary>加群请求 / 被邀请入群。</summary>
    Group = 1,
}

/// <summary>
/// 请求事件：需要机器人（或管理员）知晓的申请，如加好友、加群。
/// </summary>
/// <remarks>
/// <para>
/// 只承载数据、刻意不提供「同意 / 拒绝」能力：OneBot 12 等标准并未定义统一的请求处理动作，
/// 留一个无处落地的通道只会误导插件。适配器若确知平台支持处理请求，应另行暴露平台专属服务。
/// </para>
/// <para>
/// 同样使用「单类型 + 枚举」而非继承体系，原因见 <see cref="NoticeEvent"/> 的说明。
/// </para>
/// </remarks>
public sealed record RequestEvent : IEvent
{
    /// <summary>请求类别。</summary>
    public required RequestKind Kind { get; init; }

    /// <summary>发起请求的用户 id。</summary>
    public required string RequesterId { get; init; }

    /// <summary>群请求时的目标群 id；好友请求为 null。</summary>
    public string? GroupId { get; init; }

    /// <summary>验证消息（申请附言）；平台未提供时为 null。</summary>
    public string? Comment { get; init; }

    /// <summary>平台侧的请求标识，处理该请求时回传；平台不提供时为 null。</summary>
    public string? Flag { get; init; }

    /// <summary>群请求是否为邀请（true = 被邀请入群，false = 主动申请加群）；好友请求恒为 false。</summary>
    public bool IsInvite { get; init; }
}
