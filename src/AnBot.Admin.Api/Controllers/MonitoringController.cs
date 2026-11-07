using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Shared.Management;

namespace AnBot.Admin.Api.Controllers;

/// <summary>只读监控控制器：宿主状态、适配器、插件、命令、定时任务与审计日志。</summary>
/// <remarks>
/// 全部端点都是只读快照，不触碰消息处理热路径。
/// </remarks>
[ApiController]
[Authorize(Policy = AdminPolicies.Admin)]
[Route(AdminApiEndpointRouteBuilderExtensions.RoutePrefix)]
public sealed class MonitoringController : ControllerBase
{
    /// <summary>执行历史与审计日志单次返回的默认条数。</summary>
    private const int DefaultPageSize = 50;

    /// <summary>核心管理服务，提供运行状态快照。</summary>
    private readonly IManagementService _management;

    /// <summary>审计日志读取。</summary>
    private readonly IAuditLogStore _audit;

    /// <summary>构造函数注入管理服务与审计存储。</summary>
    /// <param name="management">核心管理服务。</param>
    /// <param name="audit">审计日志存储。</param>
    public MonitoringController(IManagementService management, IAuditLogStore audit)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentNullException.ThrowIfNull(audit);
        _management = management;
        _audit = audit;
    }

    /// <summary>宿主运行状态概览。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>状态快照。</returns>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatusAsync(CancellationToken cancellationToken)
        => Ok(await _management.GetStatusAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>已注册的适配器（平台接入点）。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>适配器列表。</returns>
    [HttpGet("adapters")]
    public async Task<IActionResult> ListAdaptersAsync(CancellationToken cancellationToken)
        => Ok(await _management.ListAdaptersAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>已加载的插件及其元数据与命令。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>插件列表。</returns>
    [HttpGet("plugins")]
    public async Task<IActionResult> ListPluginsAsync(CancellationToken cancellationToken)
        => Ok(await _management.ListPluginsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>当前可触发的全部命令名（含别名）。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>命令列表。</returns>
    [HttpGet("commands")]
    public async Task<IActionResult> ListCommandsAsync(CancellationToken cancellationToken)
        => Ok(await _management.ListCommandsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>定时任务清单及其下次触发时刻。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>定时任务列表。</returns>
    [HttpGet("scheduled-tasks")]
    public async Task<IActionResult> ListScheduledTasksAsync(CancellationToken cancellationToken)
        => Ok(await _management.ListScheduledTasksAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>最近若干条定时任务执行记录。</summary>
    /// <param name="maxCount">返回条数上限；缺省为 50。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>执行记录列表。</returns>
    [HttpGet("scheduled-tasks/executions")]
    public async Task<IActionResult> GetTaskExecutionsAsync(
        [FromQuery] int? maxCount,
        CancellationToken cancellationToken)
        => Ok(await _management
            .GetRecentTaskExecutionsAsync(maxCount ?? DefaultPageSize, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>最近的审计记录。</summary>
    /// <param name="maxCount">返回条数上限；缺省为 50。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>审计记录列表。</returns>
    [HttpGet("audit-logs")]
    public async Task<IActionResult> GetAuditLogsAsync(
        [FromQuery] int? maxCount,
        CancellationToken cancellationToken)
        => Ok(await _audit
            .GetRecentAsync(maxCount ?? DefaultPageSize, cancellationToken)
            .ConfigureAwait(false));
}
