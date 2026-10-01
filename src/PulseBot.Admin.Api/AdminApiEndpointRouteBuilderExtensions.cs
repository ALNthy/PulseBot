using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Api.Endpoints;
using PulseBot.Admin.Api.Hubs;
using PulseBot.Admin.Shared;

namespace PulseBot.Admin.Api;

/// <summary>后台 REST API 的端点映射。</summary>
public static class AdminApiEndpointRouteBuilderExtensions
{
    /// <summary>后台接口的公共前缀。</summary>
    public const string RoutePrefix = "/admin/api";

    /// <summary>
    /// 把后台接口挂到宿主的请求管道上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AdminOptions.Enabled"/> 为 <c>false</c> 时本方法直接返回，不映射任何端点——
    /// 宿主于是退化成纯机器人进程，与没有引用过后台完全一致（除了多几个未使用的注册项）。
    /// </para>
    /// <para>
    /// 健康检查 <c>/health</c> 独立于 <see cref="RoutePrefix"/>，且不需要认证：
    /// 它是给探针用的，不是后台功能的一部分。
    /// </para>
    /// </remarks>
    /// <param name="endpoints">端点构建器（由宿主传入 <c>WebApplication</c>）。</param>
    /// <returns>传入的构建器，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        AdminOptions options = endpoints.ServiceProvider.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (!options.Enabled)
        {
            return endpoints;
        }

        endpoints.MapGet("/health", static () => Results.Ok(new { status = "healthy" }));

        RouteGroupBuilder group = endpoints.MapGroup(RoutePrefix);

        // 认证端点匿名可达（登录本身不需要令牌）。
        AuthEndpoints.Map(group);

        // 监控端点统一要求持有管理员角色。
        MonitoringEndpoints.Map(group);

        // 账号管理与运行时控制：同样要求管理员角色，且每个写操作都会写审计。
        AccountEndpoints.Map(group);
        ControlEndpoints.Map(group);

        // 实时推送的 Hub。与 REST 端点一样受 Admin:Enabled 控制——
        // 后台关闭时这一段代码根本不会执行，Hub 也就不会被映射。
        endpoints.MapHub<AdminHub>(AdminHub.Route);

        return endpoints;
    }
}
