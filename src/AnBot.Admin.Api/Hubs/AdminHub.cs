using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using AnBot.Admin.Shared.Management;

namespace AnBot.Admin.Api.Hubs;

/// <summary>
/// 后台实时推送的 Hub。
/// </summary>
/// <remarks>
/// <para>
/// 服务端单向推送（日志、事件、状态），客户端不向服务端发消息——后台的写操作一律走
/// REST 接口，那条路径有统一的错误模型与审计。若把这些操作也开成 Hub 方法，
/// 同一件事就有了两个入口，两者迟早会在权限校验或审计上分叉。
/// </para>
/// <para>
/// 授权与 REST 端点同一策略：持有管理员角色即可连接。
/// </para>
/// </remarks>
[Authorize(AdminPolicies.Admin)]
public sealed class AdminHub : Hub
{
    /// <summary>Hub 的挂载路径。</summary>
    public const string Route = "/admin/hub";

    /// <summary>服务端 → 客户端：一条日志。</summary>
    public const string LogReceived = "LogReceived";

    /// <summary>服务端 → 客户端：一条事件摘要。</summary>
    public const string EventReceived = "EventReceived";

    /// <summary>服务端 → 客户端：宿主状态快照。</summary>
    public const string StatusChanged = "StatusChanged";

    private readonly IManagementService _management;

    /// <summary>构造函数注入。</summary>
    /// <param name="management">只读监控数据源，用于连接时给出首份状态。</param>
    public AdminHub(IManagementService management)
    {
        ArgumentNullException.ThrowIfNull(management);

        _management = management;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 连接建立时立刻回一份当前状态：否则客户端要等到下一个推送周期才有内容可显示，
    /// 那段空白看起来就像「连上了但没数据」。
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        HostStatusInfo status = await _management
            .GetStatusAsync(Context.ConnectionAborted)
            .ConfigureAwait(false);

        await Clients
            .Caller
            .SendAsync(StatusChanged, status, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}
