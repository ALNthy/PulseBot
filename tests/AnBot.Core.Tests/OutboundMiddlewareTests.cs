using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AnBot.Core.Adapters;
using AnBot.Core.Messages;

namespace AnBot.Core.Tests;

/// <summary>
/// 出站中间件管道测试：洋葱顺序、消息改写、阻断发送、异常隔离（fail-closed）与注册幂等。
/// </summary>
/// <remarks>
/// 适配器自身的异常隔离（<c>SendGuardedAsync</c>）不属于本文件的覆盖范围，
/// 这里只钉中间件这一层的语义。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OutboundMiddlewareTests
{
    /// <summary>中间件按优先级由外到内包裹适配器：前处理正序进入，后处理逆序返回。</summary>
    [Fact]
    public async Task 中间件按洋葱顺序包裹适配器()
    {
        var log = new List<string>();
        AdapterMessageSender sender = CreateSender(
            new RecordingAdapter(log),
            [
                new RecordingMiddleware(log, "inner"),                      // 优先级 0
                new RecordingMiddleware(log, "outer") { Priority = 10 },
            ]);

        SendResult result = await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["enter:outer", "enter:inner", "adapter", "exit:inner", "exit:outer"], log);
    }

    /// <summary>同优先级的中间件按注册顺序由外到内进入。</summary>
    [Fact]
    public async Task 同优先级的中间件按注册顺序进入()
    {
        var log = new List<string>();
        AdapterMessageSender sender = CreateSender(
            new RecordingAdapter(log),
            [
                new RecordingMiddleware(log, "first"),
                new RecordingMiddleware(log, "second"),
            ]);

        await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        Assert.Equal(["enter:first", "enter:second", "adapter", "exit:second", "exit:first"], log);
    }

    /// <summary>不调用续延即阻断：消息不会到达适配器，调用方拿到中间件自报的失败结果。</summary>
    [Fact]
    public async Task 中间件不调用续延则消息不发送()
    {
        var log = new List<string>();
        var adapter = new RecordingAdapter(log);
        AdapterMessageSender sender = CreateSender(
            adapter,
            [
                new RecordingMiddleware(log, "blocker") { Priority = 10, Block = true },
                new RecordingMiddleware(log, "never-reached"),
            ]);

        SendResult result = await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        Assert.Equal(["enter:blocker", "block:blocker"], log);
        Assert.Empty(adapter.Received);
        Assert.False(result.IsSuccess);
        Assert.Equal(SendFailureKind.Blocked, result.FailureKind);
    }

    /// <summary>中间件改写后的消息会替代原实例交给适配器。</summary>
    [Fact]
    public async Task 中间件改写的消息传给适配器()
    {
        var log = new List<string>();
        var adapter = new RecordingAdapter(log);
        AdapterMessageSender sender = CreateSender(
            adapter,
            [new RecordingMiddleware(log, "rewriter") { Rewrite = static m => m with { Message = Message.Text($"前缀：{m.Message}") } }]);

        await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        OutboundMessage sent = Assert.Single(adapter.Received);
        Assert.Equal("前缀：你好", sent.Message.ToString());
    }

    /// <summary>中间件的后处理能拿到内层的发送结果。</summary>
    [Fact]
    public async Task 中间件能读取内层的发送结果()
    {
        var log = new List<string>();
        var middleware = new RecordingMiddleware(log, "auditor");
        AdapterMessageSender sender = CreateSender(
            new RecordingAdapter(log) { Result = SendResult.Ok("platform-msg-9") },
            [middleware]);

        SendResult result = await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("platform-msg-9", middleware.CapturedResult?.MessageId);
    }

    /// <summary>
    /// 中间件异常被隔离：不外传、消息不发出（fail-closed）、调用方拿到失败结果。
    /// </summary>
    [Fact]
    public async Task 中间件异常被隔离且消息不发送()
    {
        var log = new List<string>();
        var adapter = new RecordingAdapter(log);
        AdapterMessageSender sender = CreateSender(
            adapter,
            [
                new RecordingMiddleware(log, "thrower") { Priority = 10, ThrowOnInvoke = true },
                new RecordingMiddleware(log, "inner"),
            ]);

        // 不外传：调用点不会看到异常。
        SendResult result = await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        Assert.Equal(["enter:thrower"], log);
        Assert.Empty(adapter.Received);
        Assert.False(result.IsSuccess);
        Assert.Equal(SendFailureKind.Unknown, result.FailureKind);
    }

    /// <summary>宿主关闭（令牌已取消）时的取消信号穿过中间件正常向上传播。</summary>
    [Fact]
    public async Task 宿主取消信号穿过中间件向上传播()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        AdapterMessageSender sender = CreateSender(
            new RecordingAdapter(new List<string>()),
            [new RecordingMiddleware(new List<string>(), "passthrough")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sender.SendAsync(CreateConversation(), Message.Text("你好"), cts.Token));
    }

    /// <summary>没有注册中间件时消息直接发送，且中间件链为空（快路径）。</summary>
    [Fact]
    public async Task 未注册中间件时消息直接发送()
    {
        var log = new List<string>();
        var adapter = new RecordingAdapter(log);
        AdapterMessageSender sender = CreateSender(adapter, []);

        SendResult result = await sender.SendAsync(CreateConversation(), Message.Text("你好"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["adapter"], log);
    }

    /// <summary>主动发送与被动回复两条入口都会经过中间件。</summary>
    [Fact]
    public async Task 主动发送与被动回复都经过中间件()
    {
        var log = new List<string>();
        AdapterMessageSender sender = CreateSender(
            new RecordingAdapter(log),
            [new RecordingMiddleware(log, "auditor")]);

        await sender.SendAsync(CreateConversation(), Message.Text("主动"), CancellationToken.None);
        await sender.ReplyAsync(CreateConversation(), "msg-1", Message.Text("回复"), CancellationToken.None);

        Assert.Equal(
            ["enter:auditor", "adapter", "exit:auditor", "enter:auditor", "adapter", "exit:auditor"],
            log);
    }

    /// <summary>目标平台没有注册适配器时，中间件仍会执行并看到失败结果（审计要覆盖失败路径）。</summary>
    [Fact]
    public async Task 没有适配器时中间件仍会执行()
    {
        var log = new List<string>();
        var middleware = new RecordingMiddleware(log, "auditor");
        var sender = new AdapterMessageSender([], [middleware], NullLogger<AdapterMessageSender>.Instance);

        SendResult result = await sender.SendAsync(
            new ConversationId("不存在的平台", ConversationType.Private, "u1"),
            Message.Text("你好"),
            CancellationToken.None);

        Assert.Equal(["enter:auditor", "exit:auditor"], log);
        Assert.Equal(SendFailureKind.NoAdapter, result.FailureKind);
        Assert.Equal(SendFailureKind.NoAdapter, middleware.CapturedResult?.FailureKind);
    }

    /// <summary>重复注册同一个中间件是幂等的，它只会被执行一次。</summary>
    [Fact]
    public void 重复注册中间件是幂等的()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddOutboundMiddleware<PassthroughMiddleware>();
        services.AddOutboundMiddleware<PassthroughMiddleware>();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IOutboundMiddleware>().OfType<PassthroughMiddleware>());
    }

    // ---------- 测试基础设施 ----------

    private static AdapterMessageSender CreateSender(IAdapter adapter, IOutboundMiddleware[] middlewares)
        => new([adapter], middlewares, NullLogger<AdapterMessageSender>.Instance);

    private static ConversationId CreateConversation()
        => new(RecordingAdapter.PlatformName, ConversationType.Private, "user-1");

    /// <summary>记录每次发送的适配器；可指定返回值，并在令牌已取消时中止（模拟宿主关闭）。</summary>
    private sealed class RecordingAdapter(List<string> log) : IAdapter
    {
        public const string PlatformName = "test";

        public List<OutboundMessage> Received { get; } = [];

        public SendResult Result { get; init; } = SendResult.Ok("msg-1");

        public string Name => "test-adapter";

        public string Platform => PlatformName;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Add("adapter");
            Received.Add(message);
            return ValueTask.FromResult(Result);
        }
    }

    /// <summary>记录进入/退出的中间件；可改写消息、阻断发送或抛异常。</summary>
    private sealed class RecordingMiddleware(List<string> log, string name) : IOutboundMiddleware
    {
        public int Priority { get; init; }

        public bool Block { get; init; }

        public bool ThrowOnInvoke { get; init; }

        /// <summary>不为 null 时用它改写消息再传给下游。</summary>
        public Func<OutboundMessage, OutboundMessage>? Rewrite { get; init; }

        public SendResult? CapturedResult { get; private set; }

        public async ValueTask<SendResult> InvokeAsync(
            OutboundMessage message,
            OutboundMiddlewareNext next,
            CancellationToken cancellationToken)
        {
            log.Add($"enter:{name}");

            if (ThrowOnInvoke)
            {
                throw new InvalidOperationException($"中间件 {name} 故意失败");
            }

            if (Block)
            {
                log.Add($"block:{name}");
                return SendResult.Fail(SendFailureKind.Blocked, $"被 {name} 拦截");
            }

            SendResult result = await next(Rewrite?.Invoke(message) ?? message, cancellationToken).ConfigureAwait(false);
            CapturedResult = result;
            log.Add($"exit:{name}");
            return result;
        }
    }

    /// <summary>原样放行的最小中间件；仅用于验证 DI 注册的幂等性。</summary>
    private sealed class PassthroughMiddleware : IOutboundMiddleware
    {
        public ValueTask<SendResult> InvokeAsync(
            OutboundMessage message,
            OutboundMiddlewareNext next,
            CancellationToken cancellationToken)
            => next(message, cancellationToken);
    }
}
