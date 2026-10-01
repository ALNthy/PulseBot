using System.Text.Json;

namespace PulseBot.Adapter.OneBot.V12.Protocol;

/// <summary>
/// OneBot 12 事件报文。字段是所有事件类型的并集，按 <see cref="Type"/> 与
/// <see cref="DetailType"/> 取用；对该事件无意义的字段为 null。
/// </summary>
/// <remarks>
/// 协议要求事件必带 id / time / type / detail_type / sub_type，非元事件还须带 self。
/// 这里只映射适配器真正用到的字段，其余（id、time、self 等）由序列化器忽略。
/// </remarks>
public sealed record OneBotEventDto
{
    /// <summary>事件类型：message / notice / request / meta。</summary>
    public string? Type { get; init; }

    /// <summary>事件详细类型，如 private / group / friend_increase。</summary>
    public string? DetailType { get; init; }

    /// <summary>事件子类型，如 add / invite；无子类型时实现端通常给空串。</summary>
    public string? SubType { get; init; }

    // ---------- 消息事件 ----------

    /// <summary>消息 id。</summary>
    public string? MessageId { get; init; }

    /// <summary>消息内容（消息段数组）。</summary>
    public IReadOnlyList<OneBotSegmentDto>? Message { get; init; }

    /// <summary>消息的纯文本替代表示；段数组缺失时作为回退。</summary>
    public string? AltMessage { get; init; }

    // ---------- 标识符 ----------

    /// <summary>事件主体用户 id（消息发送者、请求发起者、被影响的成员）。</summary>
    public string? UserId { get; init; }

    /// <summary>群 id。</summary>
    public string? GroupId { get; init; }

    /// <summary>频道服务器 id。</summary>
    public string? GuildId { get; init; }

    /// <summary>子频道 id。</summary>
    public string? ChannelId { get; init; }

    // ---------- 通知与请求 ----------

    /// <summary>操作者 id（撤回者、审批人等）；平台未提供时为 null。</summary>
    public string? OperatorId { get; init; }

    /// <summary>请求标识，处理请求时回传。</summary>
    public string? Flag { get; init; }

    /// <summary>请求附言（验证消息）。</summary>
    public string? Comment { get; init; }

    // ---------- 元事件 ----------

    /// <summary>心跳间隔（毫秒）；仅 heartbeat 事件有值。</summary>
    public long? Interval { get; init; }

    /// <summary>实现端版本信息；仅 connect 事件有值，只用于日志。</summary>
    public JsonElement? Version { get; init; }
}
