namespace PulseBot.Core.Messages;

/// <summary>会话类型：私聊 / 群聊 / 频道。</summary>
public enum ConversationType
{
    /// <summary>私聊（一对一）。</summary>
    Private = 0,

    /// <summary>群聊。</summary>
    Group = 1,

    /// <summary>频道（Discord Guild、QQ 频道等；第一步无适配器实现，仅留类型位）。</summary>
    Channel = 2,
}

/// <summary>
/// 跨平台会话标识：<c>平台名 + 会话类型 + 平台内会话 id</c> 三元组，
/// 是「消息发往哪里 / 事件来自哪个平台的哪个会话」的唯一答案，同时用作发送路由键。
/// </summary>
/// <remarks>
/// 用引用类型 record 而非 record struct：本类型会作为字典键参与路由，必须拒绝
/// <c>default</c>（Platform 为 null）这类非法状态；显式构造函数 + 只读属性可保证不变式，
/// 代价是每条消息一个实例的分配——在有网络 I/O 的收发路径上可忽略。
/// </remarks>
public sealed record ConversationId
{
    /// <summary>平台名（如 "onebot"），必须与 IAdapter.Platform 一致，发送时据此选择适配器。</summary>
    public string Platform { get; }

    /// <summary>会话类型。</summary>
    public ConversationType Type { get; }

    /// <summary>会话在该平台内的 id（OneBot 下为 group_id 或 user_id）。</summary>
    public string Id { get; }

    /// <summary>构造会话标识；平台名与会话 id 均不允许为空白。</summary>
    public ConversationId(string platform, ConversationType type, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Platform = platform;
        Type = type;
        Id = id;
    }

    /// <summary>形如 <c>onebot/Group/123456</c>，仅供日志与诊断，不要反向解析。</summary>
    public override string ToString() => $"{Platform}/{Type}/{Id}";
}
