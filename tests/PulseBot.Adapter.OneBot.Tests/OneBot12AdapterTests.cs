using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Adapter.OneBot.V12;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.OneBot.Tests;

/// <summary>
/// OneBot 12 适配器的端到端行为：适配器的 HttpListener 与测试的 ClientWebSocket 走真实回环，
/// 覆盖握手、事件入站、动作往返与各失败分支。
/// </summary>
[Trait("Category", "Unit")]
public sealed class OneBot12AdapterTests
{
    /// <summary>私聊消息事件进管道后，在 Core 侧得到字段完整的事件。</summary>
    [Fact]
    public async Task 私聊消息事件被翻译并发布()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);
        using ClientWebSocket client = await harness.ConnectAsync();

        await SendAsync(client, """
            {
              "id": "1",
              "time": 1700000000,
              "type": "message",
              "detail_type": "private",
              "sub_type": "",
              "message_id": "m1",
              "user_id": "10001",
              "self": { "platform": "qq", "user_id": "999" },
              "message": [ { "type": "text", "data": { "text": "你好" } } ],
              "alt_message": "你好"
            }
            """);

        MessageEvent messageEvent = await harness.Publisher.WaitForAsync<MessageEvent>();

        Assert.Equal(OneBot12Adapter.PlatformName, messageEvent.Conversation.Platform);
        Assert.Equal(ConversationType.Private, messageEvent.Conversation.Type);
        Assert.Equal("10001", messageEvent.Conversation.Id);
        Assert.Equal("m1", messageEvent.MessageId);
        Assert.Equal("10001", messageEvent.SenderId);
        Assert.Equal("你好", messageEvent.Message.ToString());
    }

    /// <summary>发送消息时发出 send_message 动作，并把响应里的消息 id 回填到结果。</summary>
    [Fact]
    public async Task 发送消息发出send_message动作并回填消息id()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);
        using ClientWebSocket client = await harness.ConnectAsync();

        ValueTask<SendResult> sending = harness.Adapter.SendAsync(
            new OutboundMessage
            {
                Conversation = new ConversationId(OneBot12Adapter.PlatformName, ConversationType.Group, "20002"),
                Message = Message.Text("回复内容"),
            },
            CancellationToken.None);

        using JsonDocument request = await ReceiveAsync(client);
        JsonElement root = request.RootElement;
        Assert.Equal("send_message", root.GetProperty("action").GetString());

        JsonElement parameters = root.GetProperty("params");
        Assert.Equal("group", parameters.GetProperty("detail_type").GetString());
        Assert.Equal("20002", parameters.GetProperty("group_id").GetString());

        JsonElement segment = Assert.Single(parameters.GetProperty("message").EnumerateArray());
        Assert.Equal("text", segment.GetProperty("type").GetString());
        Assert.Equal("回复内容", segment.GetProperty("data").GetProperty("text").GetString());

        await ReplyOkAsync(client, root, "sent-1");

        SendResult result = await sending;

        Assert.True(result.IsSuccess);
        Assert.Equal("sent-1", result.MessageId);
    }

    /// <summary>被动回复在段数组最前面插入 reply 段——OneBot 12 的回复是消息段而非独立参数。</summary>
    [Fact]
    public async Task 回复消息在段数组头部插入reply段()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);
        using ClientWebSocket client = await harness.ConnectAsync();

        ValueTask<SendResult> sending = harness.Adapter.SendAsync(
            new OutboundMessage
            {
                Conversation = new ConversationId(OneBot12Adapter.PlatformName, ConversationType.Private, "10001"),
                Message = Message.Text("收到"),
                ReplyToMessageId = "m1",
            },
            CancellationToken.None);

        using JsonDocument request = await ReceiveAsync(client);
        JsonElement[] segments = [.. request.RootElement.GetProperty("params").GetProperty("message").EnumerateArray()];

        Assert.Equal(2, segments.Length);
        Assert.Equal("reply", segments[0].GetProperty("type").GetString());
        Assert.Equal("m1", segments[0].GetProperty("data").GetProperty("message_id").GetString());
        Assert.Equal("text", segments[1].GetProperty("type").GetString());

        await ReplyOkAsync(client, request.RootElement, "sent-2");

        Assert.True((await sending).IsSuccess);
    }

    /// <summary>尚无实现端连接时立即失败，而不是把这次发送挂在那里等超时。</summary>
    [Fact]
    public async Task 没有连接时发送返回未连接()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        SendResult result = await harness.Adapter.SendAsync(
            new OutboundMessage
            {
                Conversation = new ConversationId(OneBot12Adapter.PlatformName, ConversationType.Private, "10001"),
                Message = Message.Text("无人接收"),
            },
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SendFailureKind.NotConnected, result.FailureKind);
    }

    /// <summary>实现端收下请求却不响应时按超时失败，而不是无限等待。</summary>
    [Fact]
    public async Task 实现端不响应时发送超时()
    {
        await using var harness = Harness.Create(actionTimeout: TimeSpan.FromMilliseconds(300));
        await harness.Adapter.StartAsync(CancellationToken.None);
        using ClientWebSocket client = await harness.ConnectAsync();

        ValueTask<SendResult> sending = harness.Adapter.SendAsync(
            new OutboundMessage
            {
                Conversation = new ConversationId(OneBot12Adapter.PlatformName, ConversationType.Private, "10001"),
                Message = Message.Text("石沉大海"),
            },
            CancellationToken.None);

        // 收下请求但不回应，模拟实现端卡住。
        using JsonDocument request = await ReceiveAsync(client);
        Assert.Equal("send_message", request.RootElement.GetProperty("action").GetString());

        SendResult result = await sending;

        Assert.False(result.IsSuccess);
        Assert.Equal(SendFailureKind.Timeout, result.FailureKind);
    }

    /// <summary>配置了访问令牌后，不携带令牌的连接在握手阶段就被拒绝。</summary>
    [Fact]
    public async Task 访问令牌不匹配时拒绝连接()
    {
        await using var harness = Harness.Create(accessToken: "secret");
        await harness.Adapter.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<WebSocketException>(() => harness.ConnectAsync(withToken: false));
        await Assert.ThrowsAsync<WebSocketException>(() => harness.ConnectAsync(overrideToken: "wrong"));
    }

    /// <summary>新连接到来即顶替旧连接：之后的发送走最新那条。</summary>
    [Fact]
    public async Task 新连接顶替旧连接()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        using ClientWebSocket first = await harness.ConnectAsync();
        using ClientWebSocket second = await harness.ConnectAsync();

        // 用第二条连接发一条消息事件作为同步点：适配器是先登记连接、再跑接收循环，
        // 因此「事件被处理」就意味着发送路由已经指向第二条连接。
        await SendAsync(second, """
            {
              "type": "message",
              "detail_type": "private",
              "message_id": "m1",
              "user_id": "10001",
              "message": [ { "type": "text", "data": { "text": "来自第二条连接" } } ]
            }
            """);
        MessageEvent messageEvent = await harness.Publisher.WaitForAsync<MessageEvent>();
        Assert.Equal("来自第二条连接", messageEvent.Message.ToString());

        ValueTask<SendResult> sending = harness.Adapter.SendAsync(
            new OutboundMessage
            {
                Conversation = new ConversationId(OneBot12Adapter.PlatformName, ConversationType.Private, "10001"),
                Message = Message.Text("发往最新连接"),
            },
            CancellationToken.None);

        using JsonDocument request = await ReceiveAsync(second);
        await ReplyOkAsync(second, request.RootElement, "sent-3");

        Assert.True((await sending).IsSuccess);
    }

    // ---------- 测试基础设施 ----------

    /// <summary>等待报文的超时；报文不来时明确失败，比测试挂死好诊断。</summary>
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>发送一条文本报文。</summary>
    private static async Task SendAsync(ClientWebSocket client, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await client.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>接收一条完整报文。</summary>
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket client)
    {
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await client.ReceiveAsync(buffer, timeout.Token);
            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream);
    }

    /// <summary>以成功响应回应对端刚发出的动作请求。</summary>
    private static async Task ReplyOkAsync(ClientWebSocket client, JsonElement request, string messageId)
    {
        string echo = request.GetProperty("echo").GetString()
            ?? throw new InvalidOperationException("动作请求里没有 echo，无法配对响应。");

        await SendAsync(client, $$"""
            { "status": "ok", "retcode": 0, "data": { "message_id": "{{messageId}}" }, "echo": "{{echo}}" }
            """);
    }

    /// <summary>一次装配好的适配器，监听在独立端口上，以及可供断言的记录器。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(OneBot12Adapter adapter, RecordingPublisher publisher, int port, string? accessToken)
        {
            Adapter = adapter;
            Publisher = publisher;
            Port = port;
            AccessToken = accessToken;
        }

        public OneBot12Adapter Adapter { get; }

        public RecordingPublisher Publisher { get; }

        public int Port { get; }

        public string? AccessToken { get; }

        public static Harness Create(string? accessToken = null, TimeSpan? actionTimeout = null)
        {
            int port = FindFreePort();
            var publisher = new RecordingPublisher();

            var adapter = new OneBot12Adapter(
                publisher,
                Options.Create(new OneBot12Options
                {
                    Host = "localhost",
                    Port = port,
                    Path = "/onebot/v12/",
                    AccessToken = accessToken,
                    ActionTimeout = actionTimeout ?? TimeSpan.FromSeconds(5),
                }),
                NullLogger<OneBot12Adapter>.Instance);

            return new Harness(adapter, publisher, port, accessToken);
        }

        /// <summary>以客户端身份连入，模拟 OneBot 实现端。</summary>
        /// <param name="withToken">是否携带配置里的访问令牌。</param>
        /// <param name="overrideToken">改用指定的令牌（用于验证错误令牌被拒）。</param>
        public async Task<ClientWebSocket> ConnectAsync(bool withToken = true, string? overrideToken = null)
        {
            var client = new ClientWebSocket();
            client.Options.AddSubProtocol("12.test");

            string? token = overrideToken ?? (withToken ? AccessToken : null);
            if (!string.IsNullOrEmpty(token))
            {
                client.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(new Uri($"ws://localhost:{Port}/onebot/v12/"), timeout.Token);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            return client;
        }

        public async ValueTask DisposeAsync() => await Adapter.StopAsync(CancellationToken.None);

        /// <summary>借一个空闲端口：让系统分配后立刻释放，避免与其它进程或测试冲突。</summary>
        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    /// <summary>记录收到的事件，并提供带超时的等待，避免测试因事件不来而挂死。</summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly Lock _gate = new();
        private readonly List<IEvent> _received = [];
        private readonly SemaphoreSlim _signal = new(0);

        public ValueTask PublishAsync(IEvent @event, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _received.Add(@event);
            }

            _signal.Release();
            return ValueTask.CompletedTask;
        }

        /// <summary>等到第一条指定类型的事件；超时抛 <see cref="OperationCanceledException"/>。</summary>
        public async Task<T> WaitForAsync<T>() where T : IEvent
        {
            using var timeout = new CancellationTokenSource(ReceiveTimeout);

            while (true)
            {
                lock (_gate)
                {
                    if (_received.OfType<T>().LastOrDefault() is { } found)
                    {
                        return found;
                    }
                }

                await _signal.WaitAsync(timeout.Token);
            }
        }
    }
}
