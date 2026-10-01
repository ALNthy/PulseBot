using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PulseBot.Adapter.OneBot.V12;

/// <summary>
/// 反向 WebSocket 监听器：接受 OneBot 实现的连入，完成握手与鉴权后把连接交给适配器。
/// </summary>
/// <remarks>
/// 用 <see cref="HttpListener"/> 而非第三方 WebSocket 服务器库：它随 .NET 提供、零包依赖，
/// 且这里只需要「HTTP 升级 + WebSocket 握手」这一件事，正好够用。
/// </remarks>
internal sealed class ReverseWebSocketListener : IAsyncDisposable
{
    /// <summary>鉴权头前缀。</summary>
    private const string BearerPrefix = "Bearer ";

    private readonly HttpListener _listener = new();
    private readonly string? _accessToken;
    private readonly ILogger _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="prefix">监听前缀，形如 <c>http://localhost:6700/onebot/v12/</c>。</param>
    /// <param name="accessToken">访问令牌；为空表示不校验。</param>
    /// <param name="logger">结构化日志。</param>
    public ReverseWebSocketListener(string prefix, string? accessToken, ILogger logger)
    {
        _listener.Prefixes.Add(prefix);
        _accessToken = accessToken;
        _logger = logger;
    }

    /// <summary>开始监听。前缀非法或缺少 URL ACL 权限时抛异常，由启动流程 fail fast。</summary>
    public void Start() => _listener.Start();

    /// <summary>
    /// 接受循环：每来一条连接就交给 <paramref name="onConnection"/>，循环本身不等待它结束。
    /// </summary>
    /// <remarks>
    /// 刻意不 await 连接处理：一条连接会一直占用到断开（可能数小时），
    /// await 会让接受循环卡死在这条连接上，后续连接只能排队在 <see cref="HttpListener"/> 内部。
    /// </remarks>
    public async Task AcceptLoopAsync(
        Func<WebSocket, CancellationToken, Task> onConnection,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // 监听器已停止（StopAsync 关闭了它），正常结束路径。
                _logger.LogDebug(ex, "OneBot 反向 WebSocket 监听已结束。");
                break;
            }

            _ = HandleContextAsync(context, onConnection, cancellationToken);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Close 会让挂起的 GetContextAsync 抛出，接受循环据此退出。
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>处理一次 HTTP 请求：校验、升级、交给适配器，全程异常不外泄。</summary>
    private async Task HandleContextAsync(
        HttpListenerContext context,
        Func<WebSocket, CancellationToken, Task> onConnection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!context.Request.IsWebSocketRequest)
            {
                _logger.LogWarning("收到非 WebSocket 请求，已拒绝：本适配器只接受反向 WebSocket 连接。");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            if (!IsAuthorized(context.Request))
            {
                _logger.LogWarning("OneBot 反向 WebSocket 连接鉴权失败，已拒绝。请核对配置中的 AccessToken。");
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.Close();
                return;
            }

            HttpListenerWebSocketContext webSocketContext = await context
                .AcceptWebSocketAsync(SelectSubProtocol(context.Request))
                .ConfigureAwait(false);

            _logger.LogInformation(
                "OneBot 实现端已连接（子协议：{SubProtocol}）。",
                webSocketContext.WebSocket.SubProtocol ?? "未指定");

            await onConnection(webSocketContext.WebSocket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpListenerException or OperationCanceledException)
        {
            // 握手失败或连接被中断：对端问题，不需要堆栈。
            _logger.LogDebug(ex, "OneBot 反向 WebSocket 连接未能建立或已中断。");
        }
        catch (Exception ex)
        {
            // 兜底：单条连接的意外不能影响监听循环。
            _logger.LogError(ex, "处理 OneBot 反向 WebSocket 连接时发生未预期的异常。");
        }
    }

    /// <summary>校验 <c>Authorization: Bearer &lt;token&gt;</c>；未配置令牌时一律放行。</summary>
    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            return true;
        }

        string? header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // 定长比较，避免按字节比较泄漏令牌内容。
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header[BearerPrefix.Length..]),
            Encoding.UTF8.GetBytes(_accessToken));
    }

    /// <summary>
    /// 回显客户端提供的第一个子协议（实现端会发 <c>12.&lt;impl&gt;</c>）。
    /// </summary>
    /// <remarks>
    /// 客户端要求了子协议却在响应里看不到它时，可能直接判定握手失败，因此照抄一个回去。
    /// </remarks>
    private static string? SelectSubProtocol(HttpListenerRequest request)
    {
        string? header = request.Headers["Sec-WebSocket-Protocol"];
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        int comma = header.IndexOf(',');
        string first = (comma < 0 ? header : header[..comma]).Trim();
        return first.Length == 0 ? null : first;
    }
}
