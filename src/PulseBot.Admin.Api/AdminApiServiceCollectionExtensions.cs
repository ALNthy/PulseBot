using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Infrastructure.Auth;
using PulseBot.Admin.Shared;

namespace PulseBot.Admin.Api;

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
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicies.Admin, static policy => policy.RequireRole(AdminRoles.Admin));

        return services;
    }
}
