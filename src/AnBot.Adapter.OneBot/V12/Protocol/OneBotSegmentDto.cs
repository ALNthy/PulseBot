namespace AnBot.Adapter.OneBot.V12.Protocol;

/// <summary>
/// OneBot 12 消息段：<c>{ "type": "...", "data": { ... } }</c>。
/// </summary>
public sealed record OneBotSegmentDto
{
    /// <summary>段类型名，如 text / mention / image / reply。</summary>
    public required string Type { get; init; }

    /// <summary>段参数；无参数段（如 mention_all）为空对象。</summary>
    public OneBotSegmentDataDto Data { get; init; } = new();
}

/// <summary>
/// 消息段参数的并集：按 <see cref="OneBotSegmentDto.Type"/> 取用相应字段，
/// 对该类型不适用的字段为 null。
/// </summary>
/// <remarks>
/// 用「字段并集」而非 <c>JsonElement</c> 承载 data：段类型是有限集合，
/// 并集 DTO 让映射代码可以直接读字段，不必逐层判空与类型判断。
/// </remarks>
public sealed record OneBotSegmentDataDto
{
    /// <summary>text 段的文本内容。</summary>
    public string? Text { get; init; }

    /// <summary>mention 段被提及的用户 id。</summary>
    public string? UserId { get; init; }

    /// <summary>媒体段（image / voice / audio / video / file）的文件标识。</summary>
    public string? FileId { get; init; }

    /// <summary>reply 段被引用消息的 id。</summary>
    public string? MessageId { get; init; }

    /// <summary>file 段的文件名。</summary>
    public string? Name { get; init; }

    /// <summary>location 段纬度。</summary>
    public double? Latitude { get; init; }

    /// <summary>location 段经度。</summary>
    public double? Longitude { get; init; }

    /// <summary>location 段地点名称。</summary>
    public string? Title { get; init; }
}
