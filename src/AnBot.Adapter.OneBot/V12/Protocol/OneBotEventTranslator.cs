using AnBot.Core.Events;
using AnBot.Core.Messages;

namespace AnBot.Adapter.OneBot.V12.Protocol;

/// <summary>把 OneBot 12 事件报文翻译为 Core 事件。</summary>
public static class OneBotEventTranslator
{
    /// <summary>
    /// 翻译事件；返回 null 表示该报文不应发布为 Core 事件
    /// （元事件、未知类型、或缺少定位会话所需的关键字段）。
    /// </summary>
    /// <param name="dto">事件报文。</param>
    /// <param name="platform">写入 <see cref="ConversationId.Platform"/> 的平台名，须与 <c>IAdapter.Platform</c> 一致。</param>
    public static IEvent? Translate(OneBotEventDto dto, string platform)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        return dto.Type switch
        {
            "message" => TranslateMessage(dto, platform),
            "notice" => TranslateNotice(dto, platform),
            "request" => TranslateRequest(dto),

            // 元事件（connect / heartbeat）由适配器内部消化，不进入事件管道。
            _ => null,
        };
    }

    /// <summary>把频道标识组合为 <see cref="ConversationId.Id"/>：<c>"{guild_id}:{channel_id}"</c>。</summary>
    /// <remarks>
    /// ConversationId 只有一个 id 槽位，而频道消息收发都同时需要 guild_id 与 channel_id；
    /// 用冒号拼接是无状态且可逆的表达，发送时由 <see cref="ParseChannelId"/> 拆回。
    /// </remarks>
    public static string FormatChannelId(string guildId, string channelId) => $"{guildId}:{channelId}";

    /// <summary>从 <see cref="ConversationId.Id"/> 拆回频道标识；不含分隔符时视为只有子频道 id。</summary>
    public static (string? GuildId, string ChannelId) ParseChannelId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        int separator = id.IndexOf(':');
        return separator < 0 ? (null, id) : (id[..separator], id[(separator + 1)..]);
    }

    private static MessageEvent? TranslateMessage(OneBotEventDto dto, string platform)
    {
        if (dto.UserId is null || dto.MessageId is null)
        {
            return null;
        }

        ConversationId? conversation = dto.DetailType switch
        {
            "private" => PrivateConversation(platform, dto.UserId),
            "group" => GroupConversation(platform, dto.GroupId),
            "channel" when dto.GuildId is not null && dto.ChannelId is not null
                => new ConversationId(platform, ConversationType.Channel, FormatChannelId(dto.GuildId, dto.ChannelId)),
            _ => null,
        };

        if (conversation is null)
        {
            return null;
        }

        // 实现端可能省略 message 段数组，此时退回 alt_message 的纯文本。
        Message message = dto.Message is not null
            ? OneBotMessageTranslator.ToCoreMessage(dto.Message)
            : Message.Text(dto.AltMessage ?? string.Empty);

        return new MessageEvent
        {
            Conversation = conversation,
            Message = message,
            MessageId = dto.MessageId,
            SenderId = dto.UserId,
        };
    }

    private static NoticeEvent? TranslateNotice(OneBotEventDto dto, string platform)
    {
        if (dto.DetailType is null)
        {
            return null;
        }

        (NoticeKind kind, ConversationId? conversation) = dto.DetailType switch
        {
            "friend_increase" => (NoticeKind.MemberJoined, PrivateConversation(platform, dto.UserId)),
            "friend_decrease" => (NoticeKind.MemberLeft, PrivateConversation(platform, dto.UserId)),
            "group_member_increase" => (NoticeKind.MemberJoined, GroupConversation(platform, dto.GroupId)),
            "group_member_decrease" => (NoticeKind.MemberLeft, GroupConversation(platform, dto.GroupId)),
            "group_message_delete" => (NoticeKind.MessageDeleted, GroupConversation(platform, dto.GroupId)),
            "private_message_delete" => (NoticeKind.MessageDeleted, PrivateConversation(platform, dto.UserId)),

            // 实现端扩展的通知类型：会话按群优先、私聊兜底，具体类型留在 RawType 里。
            _ => (NoticeKind.Other,
                  GroupConversation(platform, dto.GroupId) ?? PrivateConversation(platform, dto.UserId)),
        };

        if (conversation is null)
        {
            return null;
        }

        return new NoticeEvent
        {
            Conversation = conversation,
            Kind = kind,
            RawType = dto.DetailType,
            OperatorId = dto.OperatorId,
            TargetUserId = dto.UserId,
            MessageId = dto.MessageId,
        };
    }

    private static RequestEvent? TranslateRequest(OneBotEventDto dto)
    {
        if (dto.UserId is null)
        {
            return null;
        }

        return dto.DetailType switch
        {
            "friend" => new RequestEvent
            {
                Kind = RequestKind.Friend,
                RequesterId = dto.UserId,
                Comment = dto.Comment,
                Flag = dto.Flag,
            },
            "group" when dto.GroupId is not null => new RequestEvent
            {
                Kind = RequestKind.Group,
                RequesterId = dto.UserId,
                GroupId = dto.GroupId,
                Comment = dto.Comment,
                Flag = dto.Flag,
                IsInvite = string.Equals(dto.SubType, "invite", StringComparison.Ordinal),
            },
            _ => null,
        };
    }

    private static ConversationId? PrivateConversation(string platform, string? userId)
        => userId is null ? null : new ConversationId(platform, ConversationType.Private, userId);

    private static ConversationId? GroupConversation(string platform, string? groupId)
        => groupId is null ? null : new ConversationId(platform, ConversationType.Group, groupId);
}
