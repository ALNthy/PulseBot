using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AnBot.Core.Dispatching;

namespace AnBot.Core.Permissions;

/// <summary>
/// 权限域的 DI 注册扩展。
/// </summary>
public static class PermissionServiceCollectionExtensions
{
    /// <summary>
    /// 注册权限服务：权限判定实现、配置选项，以及位于事件管道最前端的接入控制拦截器。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 权限判定实现用 <c>TryAdd</c> 注册，宿主可在之后（或之前）用
    /// <c>services.AddSingleton&lt;IPermissionService, MyStore&gt;()</c> 换成持久化实现。
    /// </para>
    /// <para>
    /// 配置默认值即「无超管、无黑白名单、未声明要求的命令全部放行」，
    /// 因此不绑定配置节也能正常工作；宿主可自行
    /// <c>services.Configure&lt;PermissionOptions&gt;(config.GetSection(PermissionOptions.SectionName))</c>。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAnBotPermissions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PermissionOptions>();

        // 策略快照的持有者：读路径无锁，写路径整体替换。后台管理通过它驱动名单热更新。
        services.TryAddSingleton<IAccessPolicy, InMemoryAccessPolicy>();

        services.TryAddSingleton<IPermissionService, InMemoryPermissionService>();

        // 接入控制是消息的第一道闸门，注册为事件中间件即可，无需侵入事件分发实现。
        services.AddEventMiddleware<AccessControlMiddleware>();

        return services;
    }
}
