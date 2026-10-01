using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseBot.Core.Adapters;
using PulseBot.Core.Commands;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;
using PulseBot.Core.Permissions;
using PulseBot.Core.Plugins;
using PulseBot.Core.Sessions;

namespace PulseBot.Core.Tests;

/// <summary>
/// 交互会话的端到端测试：用真实 DI 容器装配内核，验证「命令提问 → 回答被路由回插件」的完整链路。
/// </summary>
/// <remarks>
/// 分层测试都是手工 new 对象，绕过了容器，因此发现不了循环依赖、重复注册这类只有真实装配
/// 才会暴露的问题——本文件补的就是这一层。会话拦截中间件依赖会话存储，而存储若在图里绕到适配器，
/// 就会与「适配器 → 事件入口 → 分发器 → 中间件」形成环，容器会直接拒绝解析。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SessionEndToEndTests
{
    /// <summary>容器能解析出会话存储与拦截器；若装配成环，此处即失败。</summary>
    [Fact]
    public void 内核装配后可解析会话存储与拦截器()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<ISessionStore>());
        Assert.Single(provider.GetServices<IEventMiddleware>().OfType<SessionPromptMiddleware>());
    }

    /// <summary>插件的提问命令发出问句后，用户的下一条消息被路由回该插件。</summary>
    [Fact]
    public async Task 命令提问后下一条消息路由回插件()
    {
        using ServiceProvider provider = BuildProvider();
        await StartAsync(provider);

        StubAdapter adapter = GetAdapter(provider);

        await adapter.PublishAsync(CreateMessage("/注册", "m1"), CancellationToken.None);
        await adapter.PublishAsync(CreateMessage("小明", "m2"), CancellationToken.None);

        Assert.Equal(
            [
                "reply:m1:请输入昵称",
                "reply:m2:你好，小明！请问年龄？",
            ],
            adapter.Sent);
    }

    /// <summary>连续两轮提问与回答完整走通，中途写入的会话状态在最后一轮仍读得到。</summary>
    [Fact]
    public async Task 连续两轮提问与回答完整走通()
    {
        using ServiceProvider provider = BuildProvider();
        await StartAsync(provider);

        StubAdapter adapter = GetAdapter(provider);

        await adapter.PublishAsync(CreateMessage("/注册", "m1"), CancellationToken.None);
        await adapter.PublishAsync(CreateMessage("小明", "m2"), CancellationToken.None);
        await adapter.PublishAsync(CreateMessage("18", "m3"), CancellationToken.None);

        Assert.Equal(
            [
                "reply:m1:请输入昵称",
                "reply:m2:你好，小明！请问年龄？",
                "reply:m3:小明，18 岁，登记完成。",
            ],
            adapter.Sent);
    }

    /// <summary>
    /// 被拉黑的用户即使正处在提问等待中也不会触发回调：接入控制排在会话拦截之前。
    /// </summary>
    [Fact]
    public async Task 黑名单用户在提问期间也不会触发回调()
    {
        using ServiceProvider provider = BuildProvider(new PermissionOptions { UserBlacklist = ["evil"] });
        StubAdapter adapter = GetAdapter(provider);
        var sessions = provider.GetRequiredService<ISessionStore>();

        // 预先造出等待态，否则本测试只能证明「黑名单拦截一切」，
        // 而证明不了「接入控制拦在了会话拦截前面」。
        Session session = await sessions.GetOrCreateAsync(CreateKey("evil"), CancellationToken.None);
        session.BeginPrompt("测试", static async (reply, cancellationToken) =>
        {
            await reply.ReplyAsync("不该出现", cancellationToken);
        });

        await adapter.PublishAsync(CreateMessage("你好", "m1", senderId: "evil"), CancellationToken.None);

        Assert.Empty(adapter.Sent);
    }

    /// <summary>重复装配内核是幂等的，拦截器不会被注册两次（否则一条回答会被处理两遍）。</summary>
    [Fact]
    public void 重复装配内核不会重复注册拦截器()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddPulseBot();
        services.AddPulseBot();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IEventMiddleware>().OfType<SessionPromptMiddleware>());
    }

    // ---------- 测试基础设施 ----------

    /// <summary>按宿主的方式装配：内核 + 一个依赖事件入口的桩适配器 + 一个多轮对话插件。</summary>
    private static ServiceProvider BuildProvider(PermissionOptions? permissionOptions = null)
    {
        var services = new ServiceCollection();

        // 用 NullLogger 顶替日志设施，避免为测试引入完整的日志栈。
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        if (permissionOptions is not null)
        {
            services.Configure<PermissionOptions>(options =>
                options.UserBlacklist = permissionOptions.UserBlacklist);
        }

        services.AddPulseBot();
        services.AddSingleton<IAdapter, StubAdapter>();
        services.AddPlugin<MultiTurnPlugin>();

        return services.BuildServiceProvider();
    }

    /// <summary>模拟宿主启动：加载插件，让 <c>[OnCommand]</c> 被扫描进命令表。</summary>
    private static ValueTask StartAsync(ServiceProvider provider)
        => provider.GetRequiredService<PluginManager>().LoadAllAsync(CancellationToken.None);

    private static StubAdapter GetAdapter(ServiceProvider provider)
        => (StubAdapter)provider.GetRequiredService<IEnumerable<IAdapter>>().Single();

    private static SessionKey CreateKey(string userId)
        => new(new ConversationId(StubAdapter.StubPlatform, ConversationType.Private, userId), userId);

    private static MessageEvent CreateMessage(string text, string messageId, string senderId = "u1") => new()
    {
        Conversation = new ConversationId(StubAdapter.StubPlatform, ConversationType.Private, senderId),
        Message = Message.Text(text),
        MessageId = messageId,
        SenderId = senderId,
    };

    /// <summary>演示多轮对话的插件：先问昵称，再问年龄，最后把两者拼起来回复。</summary>
    private sealed class MultiTurnPlugin(ISessionStore sessions) : IPlugin
    {
        [OnCommand("注册")]
        public async ValueTask RegisterAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Session session = await sessions
                .GetOrCreateAsync(SessionKey.From(context.Message), cancellationToken)
                .ConfigureAwait(false);

            await context.ReplyAsync("请输入昵称", cancellationToken).ConfigureAwait(false);
            session.BeginPrompt("注册.昵称", HandleNicknameAsync);
        }

        private static async ValueTask HandleNicknameAsync(SessionReplyContext reply, CancellationToken cancellationToken)
        {
            reply.Session.Set("nickname", reply.Message.PlainText.Trim());

            await reply.ReplyAsync($"你好，{reply.Message.PlainText}！请问年龄？", cancellationToken)
                .ConfigureAwait(false);

            // 继续下一轮：此时等待态已被取走，这里登记的新等待态不会被清掉。
            reply.Session.BeginPrompt("注册.年龄", HandleAgeAsync);
        }

        private static async ValueTask HandleAgeAsync(SessionReplyContext reply, CancellationToken cancellationToken)
        {
            reply.Session.TryGet<string>("nickname", out var nickname);

            await reply.ReplyAsync($"{nickname}，{reply.Message.PlainText} 岁，登记完成。", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 桩适配器：像真实适配器那样依赖事件入口，从而复现适配器与发送实现之间的双向依赖。
    /// </summary>
    private sealed class StubAdapter(IEventPublisher publisher) : IAdapter
    {
        public const string StubPlatform = "stub";

        public List<string> Sent { get; } = [];

        public string Name => "stub";

        public string Platform => StubPlatform;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
        {
            var prefix = message.IsReply ? "reply" : "send";
            Sent.Add($"{prefix}:{message.ReplyToMessageId ?? "-"}:{message.Message}");
            return ValueTask.FromResult(SendResult.Ok());
        }

        /// <summary>供测试驱动：像真实适配器那样把一条平台消息送进事件管道。</summary>
        public ValueTask PublishAsync(MessageEvent messageEvent, CancellationToken cancellationToken)
            => publisher.PublishAsync(messageEvent, cancellationToken);
    }
}
