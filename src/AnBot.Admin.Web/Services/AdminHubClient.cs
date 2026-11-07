using AnBot.Admin.Shared.Management;
using AnBot.Admin.Shared.Notifications;
using Microsoft.AspNetCore.SignalR.Client;

namespace AnBot.Admin.Web.Services;

/// <summary>
/// 后台实时推送连接（日志、事件、状态）。
/// </summary>
/// <remarks>
/// <para>
/// Hub 方法名与后端 <c>AdminHub</c> 的常量一一对应。前端不引用服务端程序集，
/// 因此这里是契约的客户端副本；改名时两侧必须同步。
/// </para>
/// <para>
/// 连接带自动重连。重新连上后 SignalR 会重新鉴权——此时 <see cref="TokenStore"/> 里的
/// 访问令牌可能已被刷新，<c>AccessTokenProvider</c> 每次连接都会重新读取，因此不需要手动更新。
/// </para>
/// </remarks>
public sealed class AdminHubClient : IAsyncDisposable
{
    private const string HubPath = "admin/hub";
    private const string LogMethod = "LogReceived";
    private const string EventMethod = "EventReceived";
    private const string StatusMethod = "StatusChanged";

    private readonly TokenStore _tokens;
    private readonly Uri _baseAddress;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private HubConnection? _connection;

    /// <summary>收到一条日志。</summary>
    public event Action<AdminLogEntry>? LogReceived;

    /// <summary>收到一条事件摘要。</summary>
    public event Action<AdminEventEntry>? EventReceived;

    /// <summary>收到状态快照。</summary>
    public event Action<HostStatusInfo>? StatusChanged;

    /// <summary>连接状态变化。</summary>
    public event Action<HubConnectionState>? StateChanged;

    /// <summary>构造函数注入令牌存储与 API 基地址。</summary>
    /// <param name="tokens">令牌存储。</param>
    /// <param name="baseAddress">API 基地址（与 HttpClient 的 BaseAddress 相同）。</param>
    public AdminHubClient(TokenStore tokens, Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(baseAddress);

        _tokens = tokens;
        _baseAddress = baseAddress;
    }

    /// <summary>当前连接状态。</summary>
    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>建立连接；已连接时幂等。未登录时调用会得到 401。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_connection is not null)
            {
                return;
            }

            string url = new Uri(_baseAddress, HubPath).ToString();

            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    // WebSocket 握手带不了 Authorization 头，后端已支持从 query string 读取。
                    options.AccessTokenProvider = () => Task.FromResult(_tokens.AccessToken);
                })
                .WithAutomaticReconnect()
                .Build();

            connection.On<AdminLogEntry>(LogMethod, entry => LogReceived?.Invoke(entry));
            connection.On<AdminEventEntry>(EventMethod, entry => EventReceived?.Invoke(entry));
            connection.On<HostStatusInfo>(StatusMethod, status => StatusChanged?.Invoke(status));

            connection.Reconnecting += _ =>
            {
                StateChanged?.Invoke(HubConnectionState.Reconnecting);
                return Task.CompletedTask;
            };

            connection.Reconnected += _ =>
            {
                StateChanged?.Invoke(HubConnectionState.Connected);
                return Task.CompletedTask;
            };

            connection.Closed += _ =>
            {
                StateChanged?.Invoke(HubConnectionState.Disconnected);
                return Task.CompletedTask;
            };

            await connection.StartAsync(cancellationToken).ConfigureAwait(false);

            _connection = connection;
            StateChanged?.Invoke(connection.State);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _startLock.Dispose();
    }
}
