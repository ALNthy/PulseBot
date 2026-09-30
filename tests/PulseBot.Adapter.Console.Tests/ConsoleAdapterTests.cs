using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.Console.Tests;

/// <summary>
/// 控制台适配器测试：输入转为事件、本地指令消化、会话切换、发送渲染与幂等性。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConsoleAdapterTests
{
    /// <summary>一行输入会变成一条消息事件，字段按配置与会话装配。</summary>
    [Fact]
    public async Task 输入一行会发布对应的消息事件()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed("/echo 你好");

        var events = await harness.Publisher.WaitForAsync(1);
        var messageEvent = Assert.Single(events);

        Assert.Equal(ConsoleAdapter.PlatformName, messageEvent.Conversation.Platform);
        Assert.Equal(ConversationType.Private, messageEvent.Conversation.Type);
        Assert.Equal("tester", messageEvent.Conversation.Id);
        Assert.Equal("tester", messageEvent.SenderId);
        Assert.Equal("/echo 你好", messageEvent.Message.ToString());
        Assert.Equal("msg-1", messageEvent.MessageId);
    }

    /// <summary>空白行被忽略，非空输入的首尾空白被去除。</summary>
    [Fact]
    public async Task 空白行被忽略且输入被去除首尾空白()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed("   ");
        harness.Input.Feed("  hello  ");

        var events = await harness.Publisher.WaitForAsync(1);

        Assert.Equal("hello", Assert.Single(events).Message.ToString());
    }

    /// <summary>多条消息的消息 id 依次递增，便于回复时定位。</summary>
    [Fact]
    public async Task 消息id依次递增()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed("first");
        harness.Input.Feed("second");

        var events = await harness.Publisher.WaitForAsync(2);

        Assert.Equal(["msg-1", "msg-2"], events.Select(e => e.MessageId));
    }

    /// <summary>exit 之后读循环结束，后续输入不再被处理。</summary>
    [Fact]
    public async Task 输入exit后不再接收消息()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed("hello");
        await harness.Publisher.WaitForAsync(1);

        harness.Input.Feed(ConsoleAdapter.ExitCommand);
        harness.Input.Feed("这条不该被处理");

        // 等到读循环真正退出，断言才可靠。
        await harness.Adapter.StopAsync(CancellationToken.None);

        Assert.Single(harness.Publisher.Received);
    }

    /// <summary>输入流结束（如被重定向的文件读完）时读循环自行退出，不抛异常。</summary>
    [Fact]
    public async Task 输入流结束后读循环自行退出()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed("hello");
        await harness.Publisher.WaitForAsync(1);

        harness.Input.End();
        await harness.Adapter.StopAsync(CancellationToken.None);

        Assert.Single(harness.Publisher.Received);
    }

    /// <summary>切换到群会话后，事件带上群标识，而发送者仍是控制台用户本人。</summary>
    [Fact]
    public async Task 切换到群会话后事件带上群标识()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed(":switch group 123456");
        harness.Input.Feed("hi");

        var events = await harness.Publisher.WaitForAsync(1);
        var messageEvent = Assert.Single(events);

        Assert.Equal(ConversationType.Group, messageEvent.Conversation.Type);
        Assert.Equal("123456", messageEvent.Conversation.Id);
        Assert.Equal("tester", messageEvent.SenderId);
    }

    /// <summary>可以切回私聊会话。</summary>
    [Fact]
    public async Task 可以切回私聊会话()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed(":switch group 123456");
        harness.Input.Feed(":switch private");
        harness.Input.Feed("hi");

        var events = await harness.Publisher.WaitForAsync(1);
        var messageEvent = Assert.Single(events);

        Assert.Equal(ConversationType.Private, messageEvent.Conversation.Type);
        Assert.Equal("tester", messageEvent.Conversation.Id);
    }

    /// <summary>本地指令在适配器内部消化，不会作为消息进入管道。</summary>
    [Fact]
    public async Task 本地指令不会发布为事件()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed(":help");
        harness.Input.Feed(":session");
        harness.Input.Feed(":switch private");
        harness.Input.Feed(":不存在的指令");
        harness.Input.Feed("marker");

        var events = await harness.Publisher.WaitForAsync(1);

        Assert.Equal("marker", Assert.Single(events).Message.ToString());
        Assert.Contains("本地指令", harness.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>非法的切换用法给出提示，且不发布事件。</summary>
    [Fact]
    public async Task 非法的切换用法被拒绝()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed(":switch");
        harness.Input.Feed(":switch group");
        harness.Input.Feed(":switch room 1");
        harness.Input.Feed("marker");

        var events = await harness.Publisher.WaitForAsync(1);

        Assert.Equal("marker", Assert.Single(events).Message.ToString());
        Assert.Contains("用法", harness.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>发送消息时按配置加上机器人前缀，并返回成功结果。</summary>
    [Fact]
    public async Task 发送消息时带上机器人前缀()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);

        var result = await harness.Adapter.SendAsync(
            new OutboundMessage
            {
                Conversation = new ConversationId(ConsoleAdapter.PlatformName, ConversationType.Private, "tester"),
                Message = Message.Text("你好"),
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("机器人> 你好", harness.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>重复启动是幂等的：只会有一个读循环。</summary>
    [Fact]
    public async Task 重复启动是幂等的()
    {
        await using var harness = Harness.Create();
        await harness.Adapter.StartAsync(CancellationToken.None);
        await harness.Adapter.StartAsync(CancellationToken.None);

        harness.Input.Feed("hello");
        await harness.Publisher.WaitForAsync(1);

        // 若真的起了两个读循环，同一次输入会被消费出两条事件。
        await Task.Delay(100);

        Assert.Single(harness.Publisher.Received);
    }

    /// <summary>未配置 UserId 属于装配错误，构造时立即失败。</summary>
    [Fact]
    public void 缺少用户id时构造失败()
    {
        Assert.Throws<ArgumentException>(() => Harness.Create(userId: " "));
    }

    // ---------- 测试基础设施 ----------

    /// <summary>一次装配好的适配器，以及可供断言的输入、输出与事件接收方。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(ConsoleAdapter adapter, ControllableInput input, StringWriter output, RecordingPublisher publisher)
        {
            Adapter = adapter;
            Input = input;
            Output = output;
            Publisher = publisher;
        }

        public ConsoleAdapter Adapter { get; }

        public ControllableInput Input { get; }

        public StringWriter Output { get; }

        public RecordingPublisher Publisher { get; }

        public static Harness Create(string userId = "tester")
        {
            var input = new ControllableInput();
            var output = new StringWriter();
            var publisher = new RecordingPublisher();

            var adapter = new ConsoleAdapter(
                publisher,
                input,
                output,
                Options.Create(new ConsoleAdapterOptions { UserId = userId }),
                NullLogger<ConsoleAdapter>.Instance);

            return new Harness(adapter, input, output, publisher);
        }

        /// <summary>结束输入并等适配器收尾，保证断言时读循环已经停止。</summary>
        public async ValueTask DisposeAsync()
        {
            Input.End();
            await Adapter.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 受控输入源：喂入的行按序取出，取空后阻塞等待（而不是返回 null），
    /// 以模拟真实控制台「一直等着下一条输入」的行为。调用 <see cref="End"/> 才结束流。
    /// </summary>
    private sealed class ControllableInput : TextReader
    {
        private readonly object _gate = new();
        private readonly Queue<string> _lines = new();
        private readonly SemaphoreSlim _available = new(0);
        private bool _ended;

        public override string? ReadLine()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_lines.Count > 0)
                    {
                        return _lines.Dequeue();
                    }

                    if (_ended)
                    {
                        return null;
                    }
                }

                _available.Wait();
            }
        }

        // 读循环是异步调用的，这里同步阻塞即可——它跑在独立线程上，不会卡住测试线程。
        public override Task<string?> ReadLineAsync() => Task.FromResult(ReadLine());

        /// <summary>喂入一行输入。</summary>
        public void Feed(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line);
            }

            _available.Release();
        }

        /// <summary>结束输入流，后续读取返回 null。</summary>
        public void End()
        {
            lock (_gate)
            {
                _ended = true;
            }

            _available.Release();
        }
    }

    /// <summary>记录收到的事件，并提供带超时的等待，避免测试因事件不来而挂死。</summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly object _gate = new();
        private readonly List<MessageEvent> _received = [];
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<MessageEvent> Received
        {
            get
            {
                lock (_gate)
                {
                    return [.. _received];
                }
            }
        }

        public ValueTask PublishAsync(IEvent @event, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _received.Add((MessageEvent)@event);
            }

            _signal.Release();
            return ValueTask.CompletedTask;
        }

        /// <summary>等待至少收到 <paramref name="count"/> 条事件；超时则返回当前已收到的，由断言给出失败信息。</summary>
        public async Task<IReadOnlyList<MessageEvent>> WaitForAsync(int count)
        {
            while (Received.Count < count)
            {
                if (!await _signal.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    break;
                }
            }

            return Received;
        }
    }
}
