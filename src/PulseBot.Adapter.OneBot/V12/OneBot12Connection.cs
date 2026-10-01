using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PulseBot.Adapter.OneBot.V12.Protocol;

namespace PulseBot.Adapter.OneBot.V12;

/// <summary>
/// 一条反向 WebSocket 连接：负责报文收发，以及动作请求与响应的配对。
/// </summary>
/// <remarks>
/// <para>
/// 连接是双向的：实现端推来的事件是「事件报文」，我们发出的动作请求由实现端以「动作响应」回答，
/// 两者共用同一条 WebSocket。报文种类靠 <c>type</c> 字段判别——协议规定事件必带它，动作响应没有。
/// </para>
/// <para>
/// 写操作由 <see cref="SemaphoreSlim"/> 串行化：一条 WebSocket 消息必须一次写完，
/// 并发调用 <see cref="WebSocketStream.CreateWritableMessageStream"/> 会让两条消息的内容交错、
/// 破坏消息边界。
/// </para>
/// </remarks>
internal sealed class OneBot12Connection : IAsyncDisposable
{
    /// <summary>发送关闭帧的最长等待时间。</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly WebSocket _socket;
    private readonly Func<OneBotEventDto, CancellationToken, ValueTask> _onEvent;
    private readonly TimeSpan _actionTimeout;
    private readonly ILogger _logger;

    /// <summary>待响应的动作请求：echo → 等待响应的任务源。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OneBotActionResponseDto>> _pending =
        new(StringComparer.Ordinal);

    /// <summary>写入门闩，保证同一时刻只有一条消息在写。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>echo 自增序号；协议要求 echo 为字符串。</summary>
    private int _echoCounter;

    private bool _disposed;

    /// <summary>构造函数注入。</summary>
    /// <param name="socket">已握手的 WebSocket。</param>
    /// <param name="onEvent">事件回调；由适配器负责翻译并送入事件管道。</param>
    /// <param name="actionTimeout">动作响应超时。</param>
    /// <param name="logger">结构化日志。</param>
    public OneBot12Connection(
        WebSocket socket,
        Func<OneBotEventDto, CancellationToken, ValueTask> onEvent,
        TimeSpan actionTimeout,
        ILogger logger)
    {
        _socket = socket;
        _onEvent = onEvent;
        _actionTimeout = actionTimeout;
        _logger = logger;
    }

    /// <summary>
    /// 接收循环：一条 WebSocket 消息 = 一个 JSON 报文，直到连接关闭或被取消。
    /// </summary>
    /// <remarks>返回前会把所有仍在等待的动作请求置为失败，避免发送方永久挂起。</remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                JsonElement? payload;
                try
                {
                    payload = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (JsonException ex)
                {
                    // 报文不是合法 JSON：属于实现端或协议层面的问题，值得被看见。
                    _logger.LogWarning(ex, "OneBot 反向 WebSocket 报文解析失败，连接结束。");
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // 对端关闭或连接中断：正常结束路径，不记为错误。
                    _logger.LogDebug(ex, "OneBot 反向 WebSocket 读取结束。");
                    break;
                }

                if (payload is not { } element || element.ValueKind != JsonValueKind.Object)
                {
                    // 流已结束（连接关闭），或收到非对象报文（协议不允许，忽略）。
                    break;
                }

                await HandlePayloadAsync(element, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            FailAllPending("OneBot 连接已关闭，未能收到动作响应。");
        }
    }

    /// <summary>
    /// 发送动作请求并等待实现端响应。
    /// </summary>
    /// <returns>实现端的响应；超时或连接在等待期间关闭时返回 null。</returns>
    public async ValueTask<OneBotActionResponseDto?> SendActionAsync<TParams>(
        string action,
        TParams parameters,
        CancellationToken cancellationToken)
    {
        string echo = Interlocked.Increment(ref _echoCounter).ToString(CultureInfo.InvariantCulture);
        var source = new TaskCompletionSource<OneBotActionResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = source;

        try
        {
            var request = new OneBotActionRequest<TParams>
            {
                Action = action,
                Params = parameters,
                Echo = echo,
            };

            await WriteMessageAsync(request, cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_actionTimeout);

            try
            {
                return await source.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("动作 {Action} 等待响应超时（echo={Echo}）。", action, echo);
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 先把等待中的请求放掉：关闭连接后它们不可能再收到响应。
        FailAllPending("OneBot 连接已关闭，未能收到动作响应。");

        try
        {
            // 只发出关闭帧，不等对端回应：对端没有在接收时永远不会回帧（连接被顶替、
            // 实现端已经走掉都是这种情况），等下去只会让新连接迟迟接不了班。
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeTimeout = new CancellationTokenSource(CloseTimeout);
                await _socket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "适配器关闭连接", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "OneBot 反向 WebSocket 关闭帧发送失败。");
        }
        finally
        {
            _socket.Dispose();
            _writeGate.Dispose();
        }
    }

    /// <summary>读取下一条报文；流已结束（连接关闭）时返回 null。</summary>
    private async ValueTask<JsonElement?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        // 每轮新建的消息流只覆盖「当前这一条」WebSocket 消息，读完自然 EOF。
        await using Stream stream = WebSocketStream.CreateReadableMessageStream(_socket);
        return await JsonSerializer
            .DeserializeAsync<JsonElement?>(stream, OneBotJson.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>写出一条报文；一条 WebSocket 消息一个流，Dispose 时写入消息结束标记。</summary>
    private async ValueTask WriteMessageAsync<T>(T payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using Stream stream = WebSocketStream.CreateWritableMessageStream(_socket, WebSocketMessageType.Text);
            await JsonSerializer.SerializeAsync(stream, payload, OneBotJson.Options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>判别报文种类并分派：有 type 的是事件，否则按动作响应处理。</summary>
    private async ValueTask HandlePayloadAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.TryGetProperty("type", out _))
        {
            OneBotEventDto? eventDto = payload.Deserialize<OneBotEventDto>(OneBotJson.Options);
            if (eventDto is not null)
            {
                await _onEvent(eventDto, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        OneBotActionResponseDto? response = payload.Deserialize<OneBotActionResponseDto>(OneBotJson.Options);
        if (response?.Echo is null)
        {
            _logger.LogDebug("收到既非事件也非动作响应的报文，已忽略。");
            return;
        }

        if (_pending.TryRemove(response.Echo, out TaskCompletionSource<OneBotActionResponseDto>? source))
        {
            source.TrySetResult(response);
        }
        else
        {
            // 通常是超时后迟到的响应，属于正常现象。
            _logger.LogDebug("收到无法匹配的动作响应（echo={Echo}），可能已超时。", response.Echo);
        }
    }

    /// <summary>让所有等待中的动作请求以异常结束，调用方据异常立即失败而非等到超时。</summary>
    private void FailAllPending(string reason)
    {
        foreach (string echo in _pending.Keys)
        {
            if (_pending.TryRemove(echo, out TaskCompletionSource<OneBotActionResponseDto>? source))
            {
                source.TrySetException(new WebSocketException(reason));
            }
        }
    }
}
