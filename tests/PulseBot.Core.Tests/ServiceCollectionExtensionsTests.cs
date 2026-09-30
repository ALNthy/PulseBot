using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseBot.Core;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Core.Tests;

/// <summary>
/// 内核装配测试：用真实 DI 容器验证「整条管道装得起来」。
/// </summary>
/// <remarks>
/// 分层单元测试都是手工 new 对象，绕过了容器，因此发现不了循环依赖、
/// 生命周期错配这类只有真实装配才会暴露的问题——本文件补的就是这一层。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ServiceCollectionExtensionsTests
{
    /// <summary>
    /// 回归测试：注册适配器后，容器仍能解析出事件入口与发送实现。
    /// </summary>
    /// <remarks>
    /// 适配器既依赖 <see cref="IEventPublisher"/>（用于发布平台事件），
    /// 又被 <see cref="AdapterMessageSender"/> 依赖（用于建立平台路由表）。
    /// 若事件入口在构造期就解析发送实现，容器会检测到
    /// 「适配器 → 事件入口 → 发送实现 → 适配器」的循环依赖并在启动时直接抛异常。
    /// </remarks>
    [Fact]
    public void 注册适配器后容器仍能解析事件入口与发送实现()
    {
        using ServiceProvider provider = BuildProvider();

        // 解析顺序刻意与宿主一致：先取全部适配器，再取事件入口。
        Assert.Single(provider.GetRequiredService<IEnumerable<IAdapter>>());
        Assert.NotNull(provider.GetRequiredService<IEventPublisher>());
        Assert.NotNull(provider.GetRequiredService<IMessageSender>());
    }

    /// <summary>
    /// 闭环验证：适配器发布的消息，其回复能经由发送实现路由回适配器自身。
    /// </summary>
    [Fact]
    public async Task 适配器事件的回复能路由回该适配器()
    {
        using ServiceProvider provider = BuildProvider();
        var adapter = (StubAdapter)provider.GetRequiredService<IEnumerable<IAdapter>>().Single();

        await adapter.PublishAsync(
            new MessageEvent
            {
                Conversation = new ConversationId(StubAdapter.StubPlatform, ConversationType.Private, "u1"),
                Message = Message.Text("你好"),
                MessageId = "msg-1",
                SenderId = "u1",
            },
            CancellationToken.None);

        // 处理器通过事件的回复通道回话，消息应经 AdapterMessageSender 回到本适配器。
        Assert.Equal(["reply:msg-1:你好"], adapter.Sent);
    }

    /// <summary>按宿主的方式装配：内核 + 一个依赖事件入口的适配器。</summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // 用 NullLogger 顶替日志设施，避免为测试引入完整的日志栈。
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddPulseBot();
        services.AddSingleton<IAdapter, StubAdapter>();

        // 一个收到消息就原样回复的处理器，用来验证回复通道确实连回了适配器。
        services.AddEventHandler<MessageEvent, EchoBackHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>收到消息就原样回复的处理器。</summary>
    private sealed class EchoBackHandler : IEventHandler<MessageEvent>
    {
        public ValueTask HandleAsync(MessageEvent @event, EventContext context, CancellationToken cancellationToken)
            => @event.ReplyAsync(@event.PlainText, cancellationToken);
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
