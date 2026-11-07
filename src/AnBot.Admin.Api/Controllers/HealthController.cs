using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnBot.Admin.Api.Controllers;

/// <summary>宿主健康探针。</summary>
/// <remarks>
/// <para>
/// 不需要认证：它是给负载均衡与容器编排探活用的，不是后台功能的一部分。
/// </para>
/// <para>
/// 路由独立于 <see cref="AdminApiEndpointRouteBuilderExtensions.RoutePrefix"/>。
/// 与其余控制器一样，只有在 <c>Admin:Enabled</c> 为 <c>true</c> 时才会被
/// <see cref="AdminApiEndpointRouteBuilderExtensions.MapAdminApi"/> 映射进管道。
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    /// <summary>返回固定存活标记。</summary>
    /// <returns><c>{"status":"healthy"}</c>。</returns>
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy" });
}
