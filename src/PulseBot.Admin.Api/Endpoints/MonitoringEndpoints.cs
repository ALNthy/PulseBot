using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PulseBot.Admin.Infrastructure.Audit;
using PulseBot.Admin.Shared.Management;

namespace PulseBot.Admin.Api.Endpoints;

/// <summary>只读监控端点：宿主状态、适配器、插件、命令、定时任务与审计日志。</summary>
/// <remarks>
/// 全部端点都是只读快照，不触碰消息处理热路径；写操作（改配置、启停插件等）不在第一期范围内。
/// </remarks>
internal static class MonitoringEndpoints
{
    /// <summary>执行历史与审计日志单次返回的默认条数。</summary>
    private const int DefaultPageSize = 50;

    /// <summary>把监控端点挂到分组上。</summary>
    /// <param name="group">后台 API 分组（前缀 <see cref="AdminApiEndpointRouteBuilderExtensions.RoutePrefix"/>）。</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/status", static async (
                IManagementService management,
                CancellationToken cancellationToken) =>
                Results.Ok(await management.GetStatusAsync(cancellationToken).ConfigureAwait(false)))
            .WithSummary("宿主运行状态概览")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapGet("/adapters", static async (
                IManagementService management,
                CancellationToken cancellationToken) =>
                Results.Ok(await management.ListAdaptersAsync(cancellationToken).ConfigureAwait(false)))
            .WithSummary("已注册的适配器（平台接入点）")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapGet("/plugins", static async (
                IManagementService management,
                CancellationToken cancellationToken) =>
                Results.Ok(await management.ListPluginsAsync(cancellationToken).ConfigureAwait(false)))
            .WithSummary("已加载的插件及其元数据与命令")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapGet("/commands", static async (
                IManagementService management,
                CancellationToken cancellationToken) =>
                Results.Ok(await management.ListCommandsAsync(cancellationToken).ConfigureAwait(false)))
            .WithSummary("当前可触发的全部命令名（含别名）")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapGet("/scheduled-tasks", static async (
                IManagementService management,
                CancellationToken cancellationToken) =>
                Results.Ok(await management.ListScheduledTasksAsync(cancellationToken).ConfigureAwait(false)))
            .WithSummary("定时任务清单及其下次触发时刻")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapGet("/scheduled-tasks/executions", static async (
                int? maxCount,
                IManagementService management,
                CancellationToken cancellationToken) =>
                Results.Ok(await management
                    .GetRecentTaskExecutionsAsync(maxCount ?? DefaultPageSize, cancellationToken)
                    .ConfigureAwait(false)))
            .WithSummary("最近若干条定时任务执行记录")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapGet("/audit-logs", static async (
                int? maxCount,
                IAuditLogStore audit,
                CancellationToken cancellationToken) =>
                Results.Ok(await audit
                    .GetRecentAsync(maxCount ?? DefaultPageSize, cancellationToken)
                    .ConfigureAwait(false)))
            .WithSummary("最近的审计记录")
            .RequireAuthorization(AdminPolicies.Admin);
    }
}
