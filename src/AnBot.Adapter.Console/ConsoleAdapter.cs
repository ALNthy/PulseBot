using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Core.Adapters;
using AnBot.Core.Dispatching;
using AnBot.Core.Events;
using AnBot.Core.Messages;

namespace AnBot.Adapter.Console;

/// <summary>
/// 控制台适配器：把标准输入/输出当作一个聊天平台。
/// </summary>
/// <remarks>
/// <para>
/// 用途有二：一是让整条链路（事件入口 → 接入控制 → 命令 → 权限 → 插件 → 回复 → 发送）
/// 在没有真实平台的情况下也能端到端跑通；二是作为新适配器的参考实现——
/// OneBot 适配器的骨架与它完全一致，只需把「读标准输入」换成「读 WebSocket 报文」。
/// </para>
/// <para>本地指令（在适配器内部消化，不会发布为事件，因而不会与插件命令冲突）：</para>
/// <code>
/// :help                    查看本地指令
/// :session                 显示当前会话
/// :switch private          切换到私聊会话
/// :switch group 123456     切换到群 123456
/// exit                     结束输入（机器人继续运行，按 Ctrl+C 退出宿主）
/// </code>
/// <para>
/// 除了上述指令，其余每一行输入都会变成一条 <see cref="MessageEvent"/> 送进事件管道；
/// 插件的回复则经由 <see cref="IMessageSender"/> 路由回 <see cref="SendAsync"/> 打印出来。
/// </para>
/// <para>
/// 线程模型：读循环跑在一个后台线程上（<see cref="StartAsync"/> 立即返回，不阻塞宿主启动），
/// 会话状态只由该循环读写，<see cref="SendAsync"/> 可能被其它线程调用，但只写输出流。
/// </para>
/// </remarks>
public sealed class ConsoleAdapter : IAdapter
{
    /// <summary>平台名；必须与事件中 <see cref="ConversationId.Platform"/> 一致，发送时据此路由。</summary>
    public const string PlatformName = "console";

    /// <summary>本地指令前缀；刻意不用 <c>/</c>，避免与插件命令前缀撞车。</summary>
    public const string LocalCommandPrefix = ":";

    /// <summary>结束输入的关键字。</summary>
    public const string ExitCommand = "exit";

    /// <summary>关闭时等待读循环收尾的上限。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IEventPublisher _publisher;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ConsoleAdapterOptions _options;
    private readonly ILogger<ConsoleAdapter> _logger;

    /// <summary>读循环的生存期令牌。刻意不复用宿主传入的取消令牌——那是启动期的，不代表运行期。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>读循环任务；非 null 即表示已启动（用作幂等标志）。</summary>
    private Task? _loop;

    /// <summary>消息 id 自增序号，控制台没有平台内消息 id，由适配器自行生成。</summary>
    private int _messageCounter;

    /// <summary>当前会话类型（可由 <c>:switch</c> 切换）。仅读循环读写。</summary>
    private ConversationType _conversationType = ConversationType.Private;

    /// <summary>当前会话 id：私聊时为配置的用户 id，群聊时为群号。仅读循环读写。</summary>
    private string _conversationId;

    /// <summary>构造函数注入。</summary>
    /// <param name="publisher">事件入口；适配器把平台事件送入 Core 的唯一通道。</param>
    /// <param name="input">输入源。宿主流向参数；测试可替换为受控的读取器。</param>
    /// <param name="output">输出目标。宿主流向参数；测试可替换为受控的写入器。</param>
    /// <param name="options">适配器配置。</param>
    /// <param name="logger">结构化日志。</param>
    public ConsoleAdapter(
        IEventPublisher publisher,
        TextReader input,
        TextWriter output,
        IOptions<ConsoleAdapterOptions> options,
        ILogger<ConsoleAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.UserId);

        _publisher = publisher;
        _input = input;
        _output = output;
        _logger = logger;

        // 初始会话：用户自己的私聊。
        _conversationId = _options.UserId;
    }

    /// <inheritdoc />
    public string Name => PlatformName;

    /// <inheritdoc />
    public string Platform => PlatformName;

    /// <summary>
    /// 启动读循环。立即返回，不阻塞宿主启动；重复调用是幂等的。
    /// </summary>
    /// <remarks>
    /// 刻意不复用 <paramref name="cancellationToken"/> 作为读循环的生存期令牌：
    /// 它来自宿主启动阶段，启动完成后不再代表「运行中」，用它会在启动一结束就误停读循环。
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop ??= Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止读循环。重复调用是幂等的。
    /// </summary>
    /// <remarks>
    /// 标准输入的阻塞读取无法被取消（见 <see cref="ReadLoopAsync"/> 的说明），
    /// 因此这里只做有限等待：若读循环正挂在一次读取上，宿主不必为它无限期等待。
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is null)
        {
            return;
        }

        try
        {
            await _loop.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 预期内：读循环正阻塞在标准输入上，取消令牌对它无效，交由进程退出回收。
            _logger.LogDebug("控制台读循环未在超时内结束，交由进程退出回收。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "控制台读循环结束时抛出未预期的异常。");
        }

        _logger.LogInformation("控制台适配器已停止接收输入。");
    }

    /// <summary>
    /// 发送消息到控制台。框架按 <see cref="ConversationId.Platform"/> 路由到这里。
    /// </summary>
    public ValueTask<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 单次 WriteLine 调用；Console.Out 自身是同步的，无须额外加锁。
        // message.Message 的 ToString 已把各消息段渲染为纯文本（图片等以占位符表示）。
        _output.WriteLine($"{_options.BotPrefix}{message.Message}");

        return ValueTask.FromResult(SendResult.Ok());
    }

    /// <summary>
    /// 读循环：打印提示符 → 读取一行 → 本地指令就自行消化，其余转为消息事件发布。
    /// </summary>
    /// <remarks>
    /// <c>ReadLineAsync</c> 不接受取消令牌——标准输入的阻塞读取在 .NET 中没有可中断的实现。
    /// 这意味着 <see cref="StopAsync"/> 无法把一次挂起的读取叫醒，只能等进程退出时回收它。
    /// 对控制台场景这是可接受的：用户关闭程序本来就靠 Ctrl+C。
    /// </remarks>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        PrintBanner();

        while (!cancellationToken.IsCancellationRequested)
        {
            _output.Write(_options.Prompt);
            _output.Flush();

            string? line = await _input.ReadLineAsync().ConfigureAwait(false);

            if (line is null)
            {
                _logger.LogInformation("控制台输入流已结束，控制台适配器不再接收消息。");
                return;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryHandleLocalCommand(line))
            {
                continue;
            }

            await PublishMessageAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 处理本地指令（<c>exit</c> 与 <c>:</c> 开头的行）。
    /// </summary>
    /// <returns>true 表示该行已被本地消化，不再作为消息发布。</returns>
    private bool TryHandleLocalCommand(string line)
    {
        if (line.Equals(ExitCommand, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "收到 {ExitCommand}，控制台适配器停止接收输入；机器人继续运行，按 Ctrl+C 退出。",
                ExitCommand);

            _cts.Cancel();
            return true;
        }

        if (!line.StartsWith(LocalCommandPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = line[LocalCommandPrefix.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return true; // 只有一个前缀，视为无意义的空指令。
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                PrintHelp();
                return true;

            case "session":
                PrintSession();
                return true;

            case "switch":
                SwitchSession(parts);
                return true;

            default:
                // 以 : 开头的行显然是在对适配器说话，而不是想发给机器人；
                // 明确反馈好过静默转发给插件。
                _output.WriteLine($"未知的本地指令：{parts[0]}（输入 :help 查看可用指令）");
                return true;
        }
    }

    /// <summary>切换当前会话：<c>:switch private</c> 或 <c>:switch group &lt;群号&gt;</c>。</summary>
    private void SwitchSession(string[] parts)
    {
        if (parts.Length == 2 && parts[1].Equals("private", StringComparison.OrdinalIgnoreCase))
        {
            _conversationType = ConversationType.Private;
            _conversationId = _options.UserId;
        }
        else if (parts.Length == 3 && parts[1].Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            _conversationType = ConversationType.Group;
            _conversationId = parts[2];
        }
        else
        {
            _output.WriteLine("用法：:switch private | :switch group <群号>");
            return;
        }

        PrintSession();
    }

    /// <summary>把一行输入转为消息事件送进管道。</summary>
    private async ValueTask PublishMessageAsync(string text, CancellationToken cancellationToken)
    {
        var messageEvent = new MessageEvent
        {
            Conversation = new ConversationId(Platform, _conversationType, _conversationId),
            Message = Message.Text(text),
            MessageId = $"msg-{Interlocked.Increment(ref _messageCounter)}",
            SenderId = _options.UserId,
            SenderDisplayName = _options.UserDisplayName,
        };

        try
        {
            await _publisher.PublishAsync(messageEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主关闭：正常控制流，向上传播。
            throw;
        }
        catch (Exception ex)
        {
            // 处理器异常已由事件分发器逐项隔离，这里是兜底：
            // 保证任何一次意外都不会终止整个读循环，否则适配器就再也收不到消息了。
            _logger.LogError(ex, "控制台输入的事件处理过程中发生未预期的异常。");
        }
    }

    /// <summary>当前会话标识。</summary>
    private ConversationId CurrentConversation => new(Platform, _conversationType, _conversationId);

    private void PrintBanner()
    {
        _output.WriteLine($"控制台适配器已启动，当前会话 {CurrentConversation}。");
        _output.WriteLine("输入 exit 结束输入，输入 :help 查看本地指令。");
    }

    private void PrintSession() => _output.WriteLine($"当前会话：{CurrentConversation}");

    private void PrintHelp()
    {
        _output.WriteLine("本地指令（在适配器内部消化，不会发送给机器人）：");
        _output.WriteLine("  :help                 显示本帮助");
        _output.WriteLine("  :session              显示当前会话");
        _output.WriteLine("  :switch private       切换到私聊会话");
        _output.WriteLine("  :switch group <群号>  切换到指定群会话");
        _output.WriteLine($"  {ExitCommand}                  结束输入（机器人继续运行，按 Ctrl+C 退出）");
        _output.WriteLine("其余输入都会作为消息发送给机器人。");
    }
}
