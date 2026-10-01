using PulseBot.Adapter.OneBot.V12;
using PulseBot.Adapter.OneBot.V12.Protocol;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.OneBot.Tests;

/// <summary>OneBot 12 事件报文到 Core 事件的翻译。</summary>
[Trait("Category", "Unit")]
public sealed class OneBotEventTranslatorTests
{
    /// <summary>适配器使用的平台名；事件里的会话必须带上它，发送时才能路由回来。</summary>
    private const string Platform = OneBot12Adapter.PlatformName;

    // ---------- 消息事件 ----------

    /// <summary>私聊消息翻译为私聊会话的消息事件。</summary>
    [Fact]
    public void 私聊消息事件翻译为私聊会话的消息事件()
    {
        var dto = new OneBotEventDto
        {
            Type = "message",
            DetailType = "private",
            MessageId = "m1",
            UserId = "10001",
            Message = [Segment("text", new OneBotSegmentDataDto { Text = "你好" })],
        };

        var messageEvent = Assert.IsType<MessageEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(Platform, messageEvent.Conversation.Platform);
        Assert.Equal(ConversationType.Private, messageEvent.Conversation.Type);
        Assert.Equal("10001", messageEvent.Conversation.Id);
        Assert.Equal("10001", messageEvent.SenderId);
        Assert.Equal("m1", messageEvent.MessageId);
        Assert.Equal("你好", messageEvent.Message.ToString());
    }

    /// <summary>群消息翻译为群会话的消息事件，群号取自 group_id。</summary>
    [Fact]
    public void 群消息事件翻译为群会话的消息事件()
    {
        var dto = new OneBotEventDto
        {
            Type = "message",
            DetailType = "group",
            MessageId = "m2",
            UserId = "10001",
            GroupId = "20002",
        };

        var messageEvent = Assert.IsType<MessageEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(ConversationType.Group, messageEvent.Conversation.Type);
        Assert.Equal("20002", messageEvent.Conversation.Id);
    }

    /// <summary>频道消息需要两个标识，会话 id 用「guild_id:channel_id」组合表达。</summary>
    [Fact]
    public void 频道消息事件使用组合会话id()
    {
        var dto = new OneBotEventDto
        {
            Type = "message",
            DetailType = "channel",
            MessageId = "m3",
            UserId = "10001",
            GuildId = "g1",
            ChannelId = "c1",
        };

        var messageEvent = Assert.IsType<MessageEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(ConversationType.Channel, messageEvent.Conversation.Type);
        Assert.Equal("g1:c1", messageEvent.Conversation.Id);
        Assert.Equal(("g1", "c1"), OneBotEventTranslator.ParseChannelId(messageEvent.Conversation.Id));
    }

    /// <summary>实现端省略消息段数组时，退回 alt_message 的纯文本。</summary>
    [Fact]
    public void 缺少消息段时回退到纯文本替代()
    {
        var dto = new OneBotEventDto
        {
            Type = "message",
            DetailType = "private",
            MessageId = "m4",
            UserId = "10001",
            AltMessage = "纯文本替代",
        };

        var messageEvent = Assert.IsType<MessageEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal("纯文本替代", messageEvent.Message.ToString());
    }

    /// <summary>缺少定位会话所需的字段时不发布，而不是造一个 id 为空的会话出来。</summary>
    [Fact]
    public void 缺少用户id的消息事件不发布()
    {
        var dto = new OneBotEventDto
        {
            Type = "message",
            DetailType = "private",
            MessageId = "m5",
        };

        Assert.Null(OneBotEventTranslator.Translate(dto, Platform));
    }

    // ---------- 通知事件 ----------

    /// <summary>群成员增加归一为成员加入，被影响的用户留在 TargetUserId。</summary>
    [Fact]
    public void 群成员增加通知翻译为成员加入()
    {
        var dto = new OneBotEventDto
        {
            Type = "notice",
            DetailType = "group_member_increase",
            GroupId = "20002",
            UserId = "10001",
            OperatorId = "10000",
        };

        var notice = Assert.IsType<NoticeEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(NoticeKind.MemberJoined, notice.Kind);
        Assert.Equal(ConversationType.Group, notice.Conversation.Type);
        Assert.Equal("20002", notice.Conversation.Id);
        Assert.Equal("10001", notice.TargetUserId);
        Assert.Equal("10000", notice.OperatorId);
        Assert.Equal("group_member_increase", notice.RawType);
    }

    /// <summary>好友增加没有群可依附，会话取与该好友的私聊。</summary>
    [Fact]
    public void 好友增加通知翻译为私聊会话的成员加入()
    {
        var dto = new OneBotEventDto
        {
            Type = "notice",
            DetailType = "friend_increase",
            UserId = "10001",
        };

        var notice = Assert.IsType<NoticeEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(NoticeKind.MemberJoined, notice.Kind);
        Assert.Equal(ConversationType.Private, notice.Conversation.Type);
        Assert.Equal("10001", notice.Conversation.Id);
    }

    /// <summary>消息撤回保留被撤回的消息 id，插件才能据此做后续处理。</summary>
    [Fact]
    public void 消息撤回通知保留消息id()
    {
        var dto = new OneBotEventDto
        {
            Type = "notice",
            DetailType = "group_message_delete",
            GroupId = "20002",
            UserId = "10001",
            OperatorId = "10000",
            MessageId = "m9",
        };

        var notice = Assert.IsType<NoticeEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(NoticeKind.MessageDeleted, notice.Kind);
        Assert.Equal("m9", notice.MessageId);
    }

    /// <summary>实现端扩展的通知类型归为 Other，原始类型留在 RawType 里供插件自行判断。</summary>
    [Fact]
    public void 未映射的通知类型归为其它()
    {
        var dto = new OneBotEventDto
        {
            Type = "notice",
            DetailType = "group_ban",
            GroupId = "20002",
            UserId = "10001",
        };

        var notice = Assert.IsType<NoticeEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(NoticeKind.Other, notice.Kind);
        Assert.Equal("group_ban", notice.RawType);
        Assert.Equal("20002", notice.Conversation.Id);
    }

    // ---------- 请求事件 ----------

    /// <summary>好友请求翻译为好友类别，附言与标识原样保留。</summary>
    [Fact]
    public void 好友请求翻译为好友请求事件()
    {
        var dto = new OneBotEventDto
        {
            Type = "request",
            DetailType = "friend",
            UserId = "10001",
            Comment = "我是小王",
            Flag = "f1",
        };

        var request = Assert.IsType<RequestEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(RequestKind.Friend, request.Kind);
        Assert.Equal("10001", request.RequesterId);
        Assert.Equal("我是小王", request.Comment);
        Assert.Equal("f1", request.Flag);
        Assert.Null(request.GroupId);
        Assert.False(request.IsInvite);
    }

    /// <summary>群请求用子类型区分「被邀请入群」与「主动申请加群」。</summary>
    [Theory]
    [InlineData("invite", true)]
    [InlineData("add", false)]
    public void 群请求用子类型区分邀请与申请(string subType, bool expectedIsInvite)
    {
        var dto = new OneBotEventDto
        {
            Type = "request",
            DetailType = "group",
            SubType = subType,
            UserId = "10001",
            GroupId = "20002",
        };

        var request = Assert.IsType<RequestEvent>(OneBotEventTranslator.Translate(dto, Platform));

        Assert.Equal(RequestKind.Group, request.Kind);
        Assert.Equal("20002", request.GroupId);
        Assert.Equal(expectedIsInvite, request.IsInvite);
    }

    // ---------- 不发布的事件 ----------

    /// <summary>元事件描述的是连接本身（connect / heartbeat），由适配器内部消化。</summary>
    [Theory]
    [InlineData("connect")]
    [InlineData("heartbeat")]
    public void 元事件不发布(string detailType)
    {
        var dto = new OneBotEventDto { Type = "meta", DetailType = detailType };

        Assert.Null(OneBotEventTranslator.Translate(dto, Platform));
    }

    /// <summary>未知事件类型不发布，避免把协议之外的报文塞进事件管道。</summary>
    [Fact]
    public void 未知事件类型不发布()
    {
        var dto = new OneBotEventDto { Type = "something_new", DetailType = "x" };

        Assert.Null(OneBotEventTranslator.Translate(dto, Platform));
    }

    // ---------- 测试基础设施 ----------

    private static OneBotSegmentDto Segment(string type, OneBotSegmentDataDto data)
        => new() { Type = type, Data = data };
}
