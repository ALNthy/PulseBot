using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseBot.Admin.Api.Hubs;
using PulseBot.Admin.Shared.Management;

namespace PulseBot.Admin.Api.Notifications;

/// <summary>
/// 把日志、事件两条通道的内容与周期性的状态快照推送给已连接的后台客户端。
/// </summary>
/// <remarks>
/// <para>
/// 读取端只有一个（本服务），写入端是宿主的日志链路与事件管道，
/// 两侧通过 <see cref="AdminNotifications"/> 的有界通道解耦——
/// 推送慢绝不会反压到消息处理上。
/// </para>
/// <para>
/// 三条推送流各自独立：某一条出错不影响另外两条，因此用三个并行的循环而不是一个
/// 大循环轮流处理——后者一旦有一条阻塞（例如状态查询变慢），另外两条也跟着停。
/// </para>
/// </remarks>
public sealed class AdminNotificationService : BackgroundService
{
    /// <summary>状态快照的推送间隔。</summary>
    /// <remarks>
    /// 状态里含运行时长，它一直在变，因此必须定期推而不是「变了才推」；
    /// 5 秒是「界面看起来是活的」与「不产生无谓流量」之间的折中。
    /// 没有客户端连接时这条推送几乎没有开销（SignalR 遍历的是空的连接表）。
    /// </remarks>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);

    private readonly AdminNotifications _notifications;
    private readonly IHubContext<AdminHub> _hub;
    private readonly IManagementService _management;
    private readonly ILogger<AdminNotificationService> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="notifications">推送缓冲。</param>
    /// <param name="hub">Hub 上下文（服务端主动推送用）。</param>
    /// <param name="management">宿主状态的只读数据源。</param>
    /// <param name="logger">结构化日志。</param>
    public AdminNotificationService(
        AdminNotifications notifications,
        IHubContext<AdminHub> hub,
        IManagementService management,
        ILogger<AdminNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(management);
        ArgumentNullException.ThrowIfNull(logger);

        _notifications = notifications;
        _hub = hub;
        _management = management;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(
            PumpAsync(_notifications.Logs, AdminHub.LogReceived, stoppingToken),
            PumpAsync(_notifications.Events, AdminHub.EventReceived, stoppingToken),
            PumpStatusAsync(stoppingToken));

    /// <summary>把一条通道的内容持续推给全部客户端。</summary>
    private async Task PumpAsync<T>(ChannelReader<T> reader, string method, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (T entry in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAsync(method, entry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主关闭：正常退出，不作为异常上报。
        }
    }

    /// <summary>定期推送宿主状态快照。</summary>
    private async Task PumpStatusAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(StatusInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                HostStatusInfo status = await _management
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);

                await SendAsync(AdminHub.StatusChanged, status, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 同上。
        }
    }

    /// <summary>广播一条消息，单次失败只记录不中断。</summary>
    /// <remarks>
    /// 推送失败最常见的原因是客户端在发送过程中断开，这不该让推送循环停下来——
    /// 停下来的后果是「之后所有连接都收不到推送」，而原因只是一个已经离开的客户端。
    /// 记录日志是安全的：本类别的日志被 <see cref="AdminLogBroadcaster"/> 排除在转发之外，
    /// 不会绕回通道形成自激。
    /// </remarks>
    private async Task SendAsync<T>(string method, T payload, CancellationToken cancellationToken)
    {
        try
        {
            await _hub
                .Clients
                .All
                .SendAsync(method, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "后台实时推送失败：{Method}。", method);
        }
    }
}
