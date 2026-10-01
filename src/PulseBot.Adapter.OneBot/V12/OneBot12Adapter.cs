using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulseBot.Adapter.OneBot.V12.Protocol;
using PulseBot.Core.Adapters;
using PulseBot.Core.Dispatching;
using PulseBot.Core.Events;
using PulseBot.Core.Messages;

namespace PulseBot.Adapter.OneBot.V12;

/// <summary>
/// OneBot 12 适配器：以反向 WebSocket 方式接入 OneBot 实现。
/// </summary>
/// <remarks>
/// <para>
/// 反向的含义是连接方向由平台侧发起：适配器启动后监听端口，OneBot 实现（NapCat、Lagrange 等）
/// 主动连入，之后同一条连接双向通行——实现端推事件，我们发动作请求。
/// </para>
/// <para>
/// 骨架与 <c>ConsoleAdapter</c> 一致：<see cref="StartAsync"/> 起后台监听后立即返回，
/// <see cref="SendAsync"/> 由框架按平台名路由。区别只在于「读标准输入」换成了「读 WebSocket 报文」。
/// </para>
/// <para>
/// 同一时刻只保留一条活跃连接：新连接到来即顶替旧连接（并让旧连接上等待中的动作请求立即失败）。
/// 这一策略对「实现端重启后重连」是必要的，否则旧连接会一直占着位置。
/// </para>
/// </remarks>
public sealed class OneBot12Adapter : IAdapter
{
    /// <summary>平台名；必须与事件中 <see cref="ConversationId.Platform"/> 一致，发送时据此路由。</summary>
    public const string PlatformName = "onebot";

    /// <summary>关闭时等待监听循环收尾的上限。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IEventPublisher _publisher;
    private readonly OneBot12Options _options;
    private readonly ILogger<OneBot12Adapter> _logger;

    /// <summary>监听循环的生存期令牌。刻意不复用宿主传入的取消令牌——那是启动期的，不代表运行期。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>保护 <see cref="_connection"/> 的替换与读取。</summary>
    private readonly Lock _connectionGate = new();

    /// <summary>当前活跃连接；无实现端连接时为 null。</summary>
    private OneBot12Connection? _connection;

    /// <summary>监听循环任务；非 null 即表示已启动（用作幂等标志）。</summary>
    private Task? _acceptLoop;

    /// <summary>构造函数注入。</summary>
    /// <param name="publisher">事件入口；适配器把平台事件送入 Core 的唯一通道。</param>
    /// <param name="options">适配器配置。</param>
    /// <param name="logger">结构化日志。</param>
    public OneBot12Adapter(
        IEventPublisher publisher,
        IOptions<OneBot12Options> options,
        ILogger<OneBot12Adapter> logger)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.Port);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.ActionTimeout, TimeSpan.Zero);

        _publisher = publisher;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PlatformName;

    /// <inheritdoc />
    public string Platform => PlatformName;

    /// <summary>
    /// 启动监听。立即返回，不阻塞宿主启动；重复调用是幂等的。
    /// </summary>
    /// <remarks>
    /// 端口占用、前缀无权限等启动期错误直接抛出，由宿主 fail fast——
    /// 静默失败会让运维以为适配器在工作，而实际上没有任何实现端能连进来。
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_acceptLoop is not null)
        {
            return Task.CompletedTask;
        }

        string prefix = BuildListenPrefix();
        var listener = new ReverseWebSocketListener(prefix, _options.AccessToken, _logger);
        listener.Start();

        _logger.LogInformation("OneBot 12 适配器已启动，等待实现端反向连接：{Prefix}", prefix);
        if (!string.IsNullOrEmpty(_options.AccessToken))
        {
            _logger.LogInformation("已启用访问令牌校验（Authorization: Bearer）。");
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止监听并断开当前连接。重复调用是幂等的。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        OneBot12Connection? connection;
        lock (_connectionGate)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is null)
        {
            return;
        }

        try
        {
            await _acceptLoop.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("OneBot 监听循环未在超时内结束，交由进程退出回收。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OneBot 监听循环结束时抛出未预期的异常。");
        }

        _logger.LogInformation("OneBot 12 适配器已停止。");
    }

    /// <summary>
    /// 发送消息给 OneBot 实现端。框架按 <see cref="ConversationId.Platform"/> 路由到这里。
    /// </summary>
    /// <remarks>按 <see cref="IAdapter"/> 契约：失败以 <see cref="SendResult"/> 返回，不抛异常。</remarks>
    public async ValueTask<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        OneBot12Connection? connection;
        lock (_connectionGate)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return SendResult.Fail(SendFailureKind.NotConnected, "当前没有 OneBot 实现端连接，消息未发送。");
        }

        try
        {
            List<OneBotSegmentDto> segments = OneBotMessageTranslator.ToOneBotSegments(message.Message);

            // OneBot 12 的「回复」是消息段而不是独立参数，因此插到段数组最前面。
            if (!string.IsNullOrEmpty(message.ReplyToMessageId))
            {
                segments.Insert(0, new OneBotSegmentDto
                {
                    Type = "reply",
                    Data = new OneBotSegmentDataDto { MessageId = message.ReplyToMessageId },
                });
            }

            OneBotSendMessageParams parameters = BuildSendMessageParams(message.Conversation, segments);
            OneBotActionResponseDto? response = await connection
                .SendActionAsync("send_message", parameters, cancellationToken)
                .ConfigureAwait(false);

            if (response is null)
            {
                return SendResult.Fail(SendFailureKind.Timeout, "等待 OneBot 实现端响应超时。");
            }

            if (!response.IsSuccess)
            {
                string reason = response.Message ?? $"实现端返回 status={response.Status} retcode={response.Retcode}。";
                return SendResult.Fail(SendFailureKind.Rejected, reason);
            }

            return SendResult.Ok(response.Data?.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：正常控制流，向上传播。
            throw;
        }
        catch (Exception ex)
        {
            // IAdapter 契约：平台异常转失败结果，不穿透到插件调用点。
            _logger.LogError(ex, "向 {Conversation} 发送消息时失败。", message.Conversation);
            return SendResult.Fail(SendFailureKind.Unknown, ex.Message);
        }
    }

    /// <summary>拼出 <see cref="HttpListener"/> 需要的监听前缀。</summary>
    private string BuildListenPrefix()
    {
        string path = _options.Path;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (!path.EndsWith('/'))
        {
            path += "/";
        }

        return $"http://{_options.Host}:{_options.Port}{path}";
    }

    /// <summary>监听循环；退出即表示适配器不再接受新连接。</summary>
    private async Task AcceptLoopAsync(ReverseWebSocketListener listener, CancellationToken cancellationToken)
    {
        await listener.AcceptLoopAsync(HandleConnectionAsync, cancellationToken).ConfigureAwait(false);
        await listener.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 接管一条新连接：顶替旧连接，然后驱动它的接收循环直到连接结束。
    /// </summary>
    private async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new OneBot12Connection(socket, HandleEventAsync, _options.ActionTimeout, _logger);

        OneBot12Connection? previous;
        lock (_connectionGate)
        {
            previous = _connection;
            _connection = connection;
        }

        if (previous is not null)
        {
            _logger.LogWarning("收到新的 OneBot 反向 WebSocket 连接，已顶替上一条连接。");
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_connectionGate)
            {
                // 只有当前连接仍是自己时才清空——期间可能已被更新的连接顶替。
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("OneBot 反向 WebSocket 连接已结束。");
    }

    /// <summary>把实现端的事件报文翻译为 Core 事件并送入事件管道。</summary>
    private async ValueTask HandleEventAsync(OneBotEventDto dto, CancellationToken cancellationToken)
    {
        // 元事件在适配器内部消化，不进入事件管道（它们描述的是连接本身，不是会话内容）。
        if (string.Equals(dto.Type, "meta", StringComparison.Ordinal))
        {
            LogMetaEvent(dto);
            return;
        }

        IEvent? coreEvent = OneBotEventTranslator.Translate(dto, Platform);
        if (coreEvent is null)
        {
            _logger.LogDebug("未发布的事件：type={Type} detail_type={DetailType}", dto.Type, dto.DetailType);
            return;
        }

        try
        {
            await _publisher.PublishAsync(coreEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主关闭：正常控制流，向上传播。
            throw;
        }
        catch (Exception ex)
        {
            // 与 ConsoleAdapter 相同的兜底：任何一次事件处理异常都不能终止接收循环，
            // 否则适配器就再也收不到消息了。
            _logger.LogError(
                ex,
                "处理 OneBot 事件（type={Type} detail_type={DetailType}）时发生未预期的异常。",
                dto.Type,
                dto.DetailType);
        }
    }

    /// <summary>记录元事件；其中 connect 携带实现端版本，值得在启动时留下一条记录。</summary>
    private void LogMetaEvent(OneBotEventDto dto)
    {
        if (string.Equals(dto.DetailType, "connect", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "OneBot 实现端上报连接（version：{Version}）。",
                dto.Version?.ToString() ?? "未提供");
        }
        else if (string.Equals(dto.DetailType, "heartbeat", StringComparison.Ordinal))
        {
            _logger.LogTrace("OneBot 心跳，间隔 {Interval} 毫秒。", dto.Interval);
        }
    }

    /// <summary>按会话类型拼出 <c>send_message</c> 的参数。</summary>
    private static OneBotSendMessageParams BuildSendMessageParams(
        ConversationId conversation,
        IReadOnlyList<OneBotSegmentDto> segments)
        => conversation.Type switch
        {
            ConversationType.Group => new OneBotSendMessageParams
            {
                DetailType = "group",
                GroupId = conversation.Id,
                Message = segments,
            },
            ConversationType.Channel => BuildChannelParams(conversation.Id, segments),

            // 私聊是默认分支：ConversationType.Private 以及将来新增的类型都落到这里，
            // 实现端会以「未知目标」拒绝，比我们自己猜一个类型更诚实。
            _ => new OneBotSendMessageParams
            {
                DetailType = "private",
                UserId = conversation.Id,
                Message = segments,
            },
        };

    /// <summary>
    /// 频道消息需要 guild_id 与 channel_id 两个标识，而只有一个是可用的。
    /// </summary>
    private static OneBotSendMessageParams BuildChannelParams(
        string conversationId,
        IReadOnlyList<OneBotSegmentDto> segments)
    {
        (string? guildId, string channelId) = OneBotEventTranslator.ParseChannelId(conversationId);

        return new OneBotSendMessageParams
        {
            DetailType = "channel",
            GuildId = guildId,
            ChannelId = channelId,
            Message = segments,
        };
    }
}
