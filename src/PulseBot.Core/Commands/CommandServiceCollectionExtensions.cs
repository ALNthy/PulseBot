using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseBot.Core.Permissions;

namespace PulseBot.Core.Commands;

/// <summary>
/// 命令分发域的 DI 注册扩展。
/// </summary>
public static class CommandServiceCollectionExtensions
{
    /// <summary>
    /// 注册命令分发服务：<see cref="CommandDispatcher"/> 单例，并把 <see cref="ICommandDispatcher"/>
    /// 转发到同一实例——运行时消费方（事件管道、插件）只看到只读接口，
    /// 启动期的注册能力（<see cref="CommandDispatcher.RegisterPluginCommands"/>）保留在具体类型上。
    /// </summary>
    /// <remarks>
    /// 命令前缀等行为由 <see cref="CommandOptions"/> 控制；宿主可绑定配置节，未绑定则使用默认值。
    /// </remarks>
    public static IServiceCollection AddPulseBotCommands(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 本域通过 IOptions<CommandOptions> 读取配置；AddOptions 幂等且轻量，
        // 确保在没有 Host 的装配场景（如单元测试）中也能解析出默认配置。
        services.AddOptions();

        // 命令分发器依赖权限判定（命令可声明 [RequirePermission]），因此顺带确保权限域在位。
        services.AddPulseBotPermissions();

        services.TryAddSingleton<CommandDispatcher>();
        services.TryAddSingleton<ICommandDispatcher>(static provider => provider.GetRequiredService<CommandDispatcher>());
        return services;
    }
}
