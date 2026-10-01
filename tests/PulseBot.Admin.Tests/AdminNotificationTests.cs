using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Api.Notifications;
using PulseBot.Admin.Shared;
using PulseBot.Admin.Shared.Notifications;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Admin.Tests;

/// <summary>
/// 实时推送的发送侧测试：有界缓冲的丢弃行为、事件摘要的映射、日志的过滤与转发。
/// </summary>
/// <remarks>
/// 只测「投递进来的是什么」这一半。另一半（把通道内容经 SignalR 送出去）需要真实宿主，
/// 由端到端验证覆盖——那是推送真正会出问题的地方（鉴权、传输、序列化），
/// 用替身测它反而测不到什么。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AdminNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>投递的日志可以按投递顺序读回。</summary>
    [Fact]
    public void 日志按投递顺序读回()
    {
        AdminNotifications notifications = new();

        notifications.Publish(new AdminLogEntry(Now, "Information", "Cat.A", "第一条"));
        notifications.Publish(new AdminLogEntry(Now, "Warning", "Cat.B", "第二条"));

        Assert.True(notifications.Logs.TryRead(out AdminLogEntry? first));
        Assert.Equal("第一条", first.Message);
        Assert.True(notifications.Logs.TryRead(out AdminLogEntry? second));
        Assert.Equal("Warning", second.Level);
        Assert.False(notifications.Logs.TryRead(out _));
    }

    /// <summary>
    /// 写满之后继续写会丢弃最旧的条目，而不是阻塞或无限增长。
    /// </summary>
    /// <remarks>
    /// 这是整个推送设计的安全边界：读取方（网络发送）一旦落后于写入方（宿主的日志与事件），
    /// 缓冲区必须有明确的封顶行为。若这里改成阻塞，后台故障就会顺着日志调用反压进消息处理。
    /// </remarks>
    [Fact]
    public void 写满之后丢弃最旧的条目()
    {
        AdminNotifications notifications = new();

        // 容量 1000：写 1001 条，最早的那条应当被挤掉。
        for (int i = 0; i < 1001; i++)
        {
            notifications.Publish(new AdminLogEntry(Now, "Information", "Cat", $"第 {i} 条"));
        }

        Assert.True(notifications.Logs.TryRead(out AdminLogEntry? oldest));
        Assert.Equal("第 1 条", oldest.Message);

        // 最后一条一定还在——丢弃的是最旧的，不是最新的。
        AdminLogEntry last = oldest;
        while (notifications.Logs.TryRead(out AdminLogEntry? entry))
        {
            last = entry;
        }

        Assert.Equal("第 1000 条", last.Message);
    }

    /// <summary>日志与事件是两条独立的通道，一边写满不影响另一边。</summary>
    [Fact]
    public void 日志与事件互不干扰()
    {
        AdminNotifications notifications = new();

        notifications.Publish(new AdminEventEntry(Now, "message", "u1", "console/Private/u1", "正文"));

        Assert.True(notifications.Events.TryRead(out AdminEventEntry? entry));
        Assert.Equal("message", entry.Type);
        Assert.False(notifications.Logs.TryRead(out _));
    }

    /// <summary>消息事件被映射为带发送者、会话与正文的摘要。</summary>
    [Fact]
    public async Task 消息事件映射为摘要()
    {
        AdminNotifications notifications = new();
        AdminEventMirror mirror = CreateMirror(notifications);

        await InvokeAsync(mirror, NewMessage("你好"));

        Assert.True(notifications.Events.TryRead(out AdminEventEntry? entry));
        Assert.Equal("message", entry.Type);
        Assert.Equal("console-user", entry.SenderId);
        Assert.Equal("console/Private/console-user", entry.Conversation);
        Assert.Equal("你好", entry.Preview);
    }

    /// <summary>正文超长时截断并加省略号，避免一条消息撑大整个推送。</summary>
    [Fact]
    public async Task 长正文被截断()
    {
        AdminNotifications notifications = new();
        AdminEventMirror mirror = CreateMirror(notifications);

        await InvokeAsync(mirror, NewMessage(new string('字', 500)));

        Assert.True(notifications.Events.TryRead(out AdminEventEntry? entry));
        Assert.Equal(201, entry.Preview?.Length);
        Assert.EndsWith("…", entry.Preview, StringComparison.Ordinal);
    }

    /// <summary>不认识的事件类型回退为 CLR 类型名，而不是被丢掉。</summary>
    [Fact]
    public async Task 未知事件类型回退为类型名()
    {
        AdminNotifications notifications = new();
        AdminEventMirror mirror = CreateMirror(notifications);

        await InvokeAsync(mirror, new UnknownEvent());

        Assert.True(notifications.Events.TryRead(out AdminEventEntry? entry));
        Assert.Equal(nameof(UnknownEvent), entry.Type);
        Assert.Null(entry.SenderId);
    }

    /// <summary>后台关闭时不复制任何事件。</summary>
    [Fact]
    public async Task 后台关闭时不复制事件()
    {
        AdminNotifications notifications = new();
        AdminEventMirror mirror = CreateMirror(notifications, enabled: false);

        await InvokeAsync(mirror, NewMessage("你好"));

        Assert.False(notifications.Events.TryRead(out _));
    }

    /// <summary>
    /// 无论是否复制，都必须调用续延——镜像若吞掉事件，机器人就再也收不到消息了。
    /// </summary>
    [Fact]
    public async Task 总是调用续延()
    {
        AdminNotifications notifications = new();
        AdminEventMirror mirror = CreateMirror(notifications, enabled: false);

        var context = new EventContext();
        bool forwarded = false;

        await mirror.InvokeAsync(
            NewMessage("你好"),
            context,
            (_, _, _) => { forwarded = true; return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.True(forwarded);
    }

    /// <summary>Information 及以上被转发。</summary>
    [Fact]
    public void 信息级别日志被转发()
    {
        AdminNotifications notifications = new();
        ILogger logger = CreateLogger(notifications, out _);

        logger.LogInformation("加载了 {Count} 个插件。", 3);

        Assert.True(notifications.Logs.TryRead(out AdminLogEntry? entry));
        Assert.Equal("Information", entry.Level);
        Assert.Equal("加载了 3 个插件。", entry.Message);
    }

    /// <summary>Debug 与 Trace 不转发：它们在框架内部极其密集，转发会让通道长期处于丢弃状态。</summary>
    [Fact]
    public void 调试级别日志不转发()
    {
        AdminNotifications notifications = new();
        ILogger logger = CreateLogger(notifications, out _);

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Warning));

        logger.LogDebug("这条不该被转发。");

        Assert.False(notifications.Logs.TryRead(out _));
    }

    /// <summary>
    /// 推送服务自身的日志不转发——否则「推送失败 → 记警告 → 又被推送 → 再失败」
    /// 会形成自我维持的循环，把通道和日志一起刷满。
    /// </summary>
    [Fact]
    public void 推送服务自身的日志不转发()
    {
        AdminNotifications notifications = new();
        AdminLogBroadcaster provider = CreateProvider(notifications, enabled: true);

        ILogger logger = provider.CreateLogger(
            typeof(AdminNotificationService).FullName ?? nameof(AdminNotificationService));

        logger.LogWarning("推送失败。");

        Assert.False(notifications.Logs.TryRead(out _));
    }

    /// <summary>后台关闭时完全不转发，连类别判断都不做。</summary>
    [Fact]
    public void 后台关闭时不转发日志()
    {
        AdminNotifications notifications = new();
        ILogger logger = CreateLogger(notifications, out _, enabled: false);

        Assert.False(logger.IsEnabled(LogLevel.Error));
        logger.LogError("这条不该被转发。");

        Assert.False(notifications.Logs.TryRead(out _));
    }

    /// <summary>异常文本并入正文，不单独占一个字段。</summary>
    [Fact]
    public void 异常并入日志正文()
    {
        AdminNotifications notifications = new();
        ILogger logger = CreateLogger(notifications, out _);

        logger.LogError(new InvalidOperationException("底层失败"), "处理消息时出错。");

        Assert.True(notifications.Logs.TryRead(out AdminLogEntry? entry));
        Assert.Contains("处理消息时出错。", entry.Message, StringComparison.Ordinal);
        Assert.Contains("底层失败", entry.Message, StringComparison.Ordinal);
    }

    // ---------- 测试基础设施 ----------

    private static AdminEventMirror CreateMirror(AdminNotifications notifications, bool enabled = true)
        => new(notifications, Options.Create(new AdminOptions { Enabled = enabled }), new MutableTimeProvider(Now));

    private static AdminLogBroadcaster CreateProvider(AdminNotifications notifications, bool enabled)
        => new(notifications, Options.Create(new AdminOptions { Enabled = enabled }), new MutableTimeProvider(Now));

    private static ILogger CreateLogger(
        AdminNotifications notifications,
        out AdminLogBroadcaster provider,
        bool enabled = true)
    {
        provider = CreateProvider(notifications, enabled);
        return provider.CreateLogger("PulseBot.Tests");
    }

    private static async Task InvokeAsync(IEventMiddleware middleware, IEvent @event)
        => await middleware.InvokeAsync(
            @event,
            new EventContext(),
            static (_, _, _) => ValueTask.CompletedTask,
            CancellationToken.None);

    private static MessageEvent NewMessage(string text) => new()
    {
        Conversation = new ConversationId("console", ConversationType.Private, "console-user"),
        Message = Message.Text(text),
        MessageId = "msg-1",
        SenderId = "console-user",
    };

    /// <summary>测试用的未知事件类型：验证回退路径。</summary>
    private sealed record UnknownEvent : IEvent;
}
