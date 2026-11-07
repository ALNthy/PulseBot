using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnBot.Adapter.OneBot.V12.Protocol;

/// <summary>OneBot 12 报文的 JSON 序列化约定。</summary>
internal static class OneBotJson
{
    /// <summary>
    /// 协议字段全部是 snake_case（user_id、detail_type 等），
    /// 因此用命名策略统一转换，DTO 属性保持 C# 的 PascalCase。
    /// 写出时忽略 null：DTO 是各类型的「字段并集」，不忽略会把无意义的空字段也发出去。
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
