using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PulseBot.Admin.Infrastructure.Control;
using PulseBot.Admin.Shared.Accounts;
using PulseBot.Admin.Shared.Control;

namespace PulseBot.Admin.Api.Endpoints;

/// <summary>运行时控制端点：权限名单热更新与插件启停。</summary>
/// <remarks>
/// <para>
/// 这两类改动都会立刻作用到内核正在使用的数据上（名单快照、插件状态注册表），
/// 因此不提供「暂存/生效」两步式接口——一次调用就是一次生效，
/// 与审计记录一一对应，回头看审计时能准确知道哪一次改动造成了当时的行为。
/// </para>
/// </remarks>
internal static class ControlEndpoints
{
    /// <summary>把控制端点挂到分组上。</summary>
    /// <param name="group">后台 API 分组（前缀 <see cref="AdminApiEndpointRouteBuilderExtensions.RoutePrefix"/>）。</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/access-policy", GetAccessPolicyAsync)
            .WithSummary("读取当前生效的权限名单与拒绝提示")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapPut("/access-policy/{listKey}", UpdateAccessPolicyAsync)
            .WithSummary("整体替换一份权限名单，立即生效并持久化；返回替换后的完整策略")
            .RequireAuthorization(AdminPolicies.Admin);

        group.MapPut("/plugins/{typeName}/state", SetPluginStateAsync)
            .WithSummary("启用或停用插件（停用后其注册的命令不可触发），重启后保持")
            .RequireAuthorization(AdminPolicies.Admin);
    }

    private static async Task<IResult> GetAccessPolicyAsync(
        IRuntimeControlService runtimeControl,
        CancellationToken cancellationToken)
    {
        AccessPolicyInfo policy = await runtimeControl
            .GetAccessPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(policy);
    }

    private static async Task<IResult> UpdateAccessPolicyAsync(
        string listKey,
        UpdatePermissionListRequest request,
        HttpContext http,
        IRuntimeControlService runtimeControl,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || request.Values is null)
        {
            // 空数组是合法的（表示清空这份名单），缺字段不是：前者是明确的意图，
            // 后者多半是客户端拼错了 JSON，当成「清空」执行会把名单无声抹掉。
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "缺少 Values 字段。清空名单请显式传空数组。"));
        }

        RuntimeControlStatus status = await runtimeControl
            .UpdateListAsync(actor, listKey, request.Values, cancellationToken)
            .ConfigureAwait(false);

        if (status != RuntimeControlStatus.Success)
        {
            return DescribeFailure(status);
        }

        // 回读当前生效的快照而不是回显请求体：前端一次调用后就能拿到权威状态，
        // 也就不会出现「界面显示的和内核实际用的不一致」。
        AccessPolicyInfo policy = await runtimeControl
            .GetAccessPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(policy);
    }

    private static async Task<IResult> SetPluginStateAsync(
        string typeName,
        SetPluginStateRequest request,
        HttpContext http,
        IRuntimeControlService runtimeControl,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(http, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null)
        {
            return Results.BadRequest(new ApiError(ApiErrorCodes.InvalidRequest, "缺少 Enabled 字段。"));
        }

        RuntimeControlStatus status = await runtimeControl
            .SetPluginEnabledAsync(actor, typeName, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        return status == RuntimeControlStatus.Success
            ? Results.NoContent()
            : DescribeFailure(status);
    }

    /// <summary>把控制器返回的失败原因翻译成 HTTP 响应。</summary>
    private static IResult DescribeFailure(RuntimeControlStatus status) => status switch
    {
        RuntimeControlStatus.UnknownList => ApiResponses.Failure(
            StatusCodes.Status404NotFound, ApiErrorCodes.TargetNotFound, "名单标识不存在。可用标识见 GET /access-policy 返回的 lists[].key。"),

        RuntimeControlStatus.PluginNotFound => ApiResponses.Failure(
            StatusCodes.Status404NotFound, ApiErrorCodes.TargetNotFound, "插件类型名不存在。类型名须与 GET /plugins 返回的 TypeName 完全一致。"),

        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "未知的运行时控制结果：接口层没有为它定义响应。"),
    };

}
