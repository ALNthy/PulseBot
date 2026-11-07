using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Admin.Api.Hubs;
using AnBot.Admin.Api.Notifications;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Api;

/// <summary>后台 REST API 的依赖注入装配。</summary>
public static class AdminApiServiceCollectionExtensions
{
    /// <summary>
    /// 注册后台 API 需要的服务：JWT 认证与授权策略。
    /// </summary>
    /// <remarks>
    /// <para>调用方应同时调用 <c>AddAdminInfrastructure()</c>——本方法依赖它注册的 <see cref="TokenService"/>。</para>
    /// <para>
    /// 令牌的签发（<see cref="TokenService.CreateAccessToken"/>）与校验共用
    /// <see cref="TokenService.CreateValidationParameters"/> 同一份参数，
    /// 避免出现「签得出、验不过」这类两边配置漂移的问题。
    /// </para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddAdminApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 后台 REST 端点以 MVC 控制器形态提供（见 Controllers/ 目录）。
        // Admin.Api 是类库，而宿主的入口程序集是 AnBot.Cli——MVC 默认只扫描入口程序集，
        // 因此必须用 AddApplicationPart 显式登记本程序集，否则控制器静默不生效（全部 404）。
        services.AddControllers()
            .AddApplicationPart(typeof(AdminApiServiceCollectionExtensions).Assembly)
            .ConfigureApiBehaviorOptions(static options =>
            {
                // [ApiController] 默认用 ProblemDetails 表达模型绑定错误。后台对外只暴露
                // ApiError 一种错误形状，这里统一过去，调用方不必按端点分辨两种错误格式。
                options.InvalidModelStateResponseFactory = static _ =>
                    new BadRequestObjectResult(new ApiError(
                        ApiErrorCodes.InvalidRequest,
                        "请求体格式不正确或缺少必需字段。"));
            });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // 校验参数要等到容器可解析时才拿得到，因此用带依赖的 Configure 重载而不是 lambda 闭包。
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IServiceProvider>(static (options, provider) =>
            {
                TokenService tokens = provider.GetRequiredService<TokenService>();

                // 声明名保持 JWT 里的原样（name / role），不做 .NET 传统声明类型映射：
                // 令牌是我们自己签的，映射只会让排查问题时多一层心智负担。
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.CreateValidationParameters();

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = static context =>
                    {
                        // WebSocket 握手由浏览器发起，带不了 Authorization 头，只能把令牌放在
                        // 查询串里——这是 SignalR 官方文档给出的做法。
                        // 仅对 Hub 路径生效：其余端点仍走标准头，免得令牌出现在会被访问日志、
                        // 浏览器历史与代理日志记录的 URL 上。
                        string? accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments(AdminHub.Route))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicies.Admin, static policy => policy.RequireRole(AdminRoles.Admin));

        // 实时推送。三者职责不同：缓冲负责解耦与限流，广播器负责把宿主日志接进来，
        // 推送服务负责把缓冲里的内容送出去。
        services.AddSignalR();
        services.AddSingleton<AdminNotifications>();

        // 注册为 ILoggerProvider 就会被日志系统自动接上（LoggerFactory 从容器解析全部该接口实现），
        // 因此宿主不需要再调 AddProvider。后台关闭时它自己判断并完全不转发。
        services.AddSingleton<ILoggerProvider, AdminLogBroadcaster>();
        services.AddHostedService<AdminNotificationService>();

        return services;
    }
}
