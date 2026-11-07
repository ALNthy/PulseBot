using System.Text.Json.Serialization;

namespace AnBot.Adapter.OneBot.V12.Protocol;

/// <summary>动作请求：<c>{ "action": "...", "params": { ... }, "echo": "..." }</c>。</summary>
/// <typeparam name="TParams">动作参数类型；各动作参数形状不同，由调用方给出。</typeparam>
public sealed record OneBotActionRequest<TParams>
{
    /// <summary>动作名，如 send_message。</summary>
    public required string Action { get; init; }

    /// <summary>动作参数。</summary>
    public required TParams Params { get; init; }

    /// <summary>请求标识，响应会原样返回，用于匹配并发的请求。</summary>
    public required string Echo { get; init; }
}

/// <summary><c>send_message</c> 动作的参数。</summary>
public sealed record OneBotSendMessageParams
{
    /// <summary>会话类型：private / group / channel。</summary>
    public required string DetailType { get; init; }

    /// <summary>私聊目标用户 id；<see cref="DetailType"/> 为 private 时提供。</summary>
    public string? UserId { get; init; }

    /// <summary>群 id；<see cref="DetailType"/> 为 group 时提供。</summary>
    public string? GroupId { get; init; }

    /// <summary>频道服务器 id；<see cref="DetailType"/> 为 channel 时提供。</summary>
    public string? GuildId { get; init; }

    /// <summary>子频道 id；<see cref="DetailType"/> 为 channel 时提供。</summary>
    public string? ChannelId { get; init; }

    /// <summary>消息内容（消息段数组）。</summary>
    public required IReadOnlyList<OneBotSegmentDto> Message { get; init; }
}

/// <summary>
/// 动作响应：<c>{ "status": "ok", "retcode": 0, "data": { ... }, "message": "...", "echo": "..." }</c>。
/// </summary>
public sealed record OneBotActionResponseDto
{
    /// <summary>执行状态："ok" 或 "failed"。</summary>
    public string? Status { get; init; }

    /// <summary>响应码：0 表示成功，非 0 为错误码（1xxxx 系列）。</summary>
    public int? Retcode { get; init; }

    /// <summary>响应数据；形状随动作而异，这里只映射 send_message 用到的部分。</summary>
    public OneBotActionDataDto? Data { get; init; }

    /// <summary>错误 / 状态描述。</summary>
    public string? Message { get; init; }

    /// <summary>原样返回的请求标识。</summary>
    public string? Echo { get; init; }

    /// <summary>动作是否成功执行（协议约定 status 为 ok 且 retcode 为 0）。</summary>
    [JsonIgnore]
    public bool IsSuccess => string.Equals(Status, "ok", StringComparison.Ordinal) && Retcode == 0;
}

/// <summary>动作响应 <c>data</c> 的并集（当前只用到 send_message 返回的消息 id）。</summary>
public sealed record OneBotActionDataDto
{
    /// <summary>发送成功后平台返回的消息 id。</summary>
    public string? MessageId { get; init; }
}
