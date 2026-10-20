using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PulseBot.Admin.Infrastructure.Control;
using PulseBot.Admin.Shared.Accounts;
using PulseBot.Admin.Shared.Control;

namespace PulseBot.Admin.Api.Controllers;

/// <summary>运行时控制控制器：权限名单热更新与插件启停。</summary>
/// <remarks>
/// <para>
/// 这两类改动都会立刻作用到内核正在使用的数据上（名单快照、插件状态注册表），
/// 因此不提供「暂存/生效」两步式接口——一次调用就是一次生效，
/// 与审计记录一一对应，回头看审计时能准确知道哪一次改动造成了当时的行为。
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AdminPolicies.Admin)]
[Route(AdminApiEndpointRouteBuilderExtensions.RoutePrefix)]
public sealed class ControlController : ControllerBase
{
    /// <summary>运行时控制服务，负责落库、生效与审计。</summary>
    private readonly IRuntimeControlService _runtimeControl;

    /// <summary>构造函数注入运行时控制服务。</summary>
    /// <param name="runtimeControl">运行时控制服务。</param>
    public ControlController(IRuntimeControlService runtimeControl)
    {
        ArgumentNullException.ThrowIfNull(runtimeControl);
        _runtimeControl = runtimeControl;
    }

    /// <summary>读取当前生效的权限名单与拒绝提示。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>当前策略快照。</returns>
    [HttpGet("access-policy")]
    public async Task<IActionResult> GetAccessPolicyAsync(CancellationToken cancellationToken)
    {
        AccessPolicyInfo policy = await _runtimeControl
            .GetAccessPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(policy);
    }

    /// <summary>整体替换一份权限名单，立即生效并持久化。</summary>
    /// <param name="listKey">名单标识（超管、用户白/黑名单、群白/黑名单）。</param>
    /// <param name="request">新的完整名单。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>替换后的完整策略；名单标识不存在为 404。</returns>
    [HttpPut("access-policy/{listKey}")]
    public async Task<IActionResult> UpdateAccessPolicyAsync(
        [FromRoute] string listKey,
        [FromBody] UpdatePermissionListRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null || request.Values is null)
        {
            // 空数组是合法的（表示清空这份名单），缺字段不是：前者是明确的意图，
            // 后者多半是客户端拼错了 JSON，当成「清空」执行会把名单无声抹掉。
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "缺少 Values 字段。清空名单请显式传空数组。");
        }

        RuntimeControlStatus status = await _runtimeControl
            .UpdateListAsync(actor, listKey, request.Values, cancellationToken)
            .ConfigureAwait(false);

        if (status != RuntimeControlStatus.Success)
        {
            return DescribeFailure(status);
        }

        // 回读当前生效的快照而不是回显请求体：前端一次调用后就能拿到权威状态，
        // 也就不会出现「界面显示的和内核实际用的不一致」。
        AccessPolicyInfo policy = await _runtimeControl
            .GetAccessPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(policy);
    }

    /// <summary>启用或停用插件（停用后其注册的命令不可触发），重启后保持。</summary>
    /// <param name="typeName">插件类型全名，须与 <c>GET /plugins</c> 返回的 <c>TypeName</c> 完全一致。</param>
    /// <param name="request">目标状态。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>成功为 204；插件类型名不存在为 404。</returns>
    [HttpPut("plugins/{typeName}/state")]
    public async Task<IActionResult> SetPluginStateAsync(
        [FromRoute] string typeName,
        [FromBody] SetPluginStateRequest request,
        CancellationToken cancellationToken)
    {
        if (!ApiResponses.TryGetActor(HttpContext, out AccountActor? actor))
        {
            return ApiResponses.Failure(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidToken, "访问令牌缺少身份信息，请重新登录。");
        }

        if (request is null)
        {
            return ApiResponses.Failure(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "缺少 Enabled 字段。");
        }

        RuntimeControlStatus status = await _runtimeControl
            .SetPluginEnabledAsync(actor, typeName, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        return status == RuntimeControlStatus.Success
            ? NoContent()
            : DescribeFailure(status);
    }

    /// <summary>把运行时控制结果翻译成 HTTP 响应。</summary>
    private static IActionResult DescribeFailure(RuntimeControlStatus status) => status switch
    {
        RuntimeControlStatus.UnknownList => ApiResponses.Failure(
            StatusCodes.Status404NotFound, ApiErrorCodes.TargetNotFound, "名单标识不存在。可用标识见 GET /access-policy 返回的 lists[].key。"),

        RuntimeControlStatus.PluginNotFound => ApiResponses.Failure(
            StatusCodes.Status404NotFound, ApiErrorCodes.TargetNotFound, "插件类型名不存在。类型名须与 GET /plugins 返回的 TypeName 完全一致。"),

        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "未知的运行时控制结果：接口层没有为它定义响应。"),
    };
}
