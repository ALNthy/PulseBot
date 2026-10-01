using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseBot.Core.Dispatching;

namespace PulseBot.Core.Sessions;

/// <summary>
/// 交互会话域的 DI 注册扩展。
/// </summary>
public static class SessionServiceCollectionExtensions
{
    /// <summary>
    /// 注册交互会话：会话存储、配置选项，以及把「等待中的回答」路由回插件的消息拦截器。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 会话存储用 <c>TryAdd</c> 注册，宿主可换成自己的实现（但注意
    /// <see cref="ISessionStore"/> 对持久化的限制说明）。
    /// </para>
    /// <para>
    /// 时间源注册为 <see cref="TimeProvider.System"/>，用 <c>TryAdd</c> 是为了让测试或宿主
    /// 能够覆盖（<c>services.AddSingleton&lt;TimeProvider&gt;(可控实现)</c>），
    /// 否则会话过期就无法在测试里稳定复现。
    /// </para>
    /// <para>
    /// 拦截器的优先级（<c>int.MaxValue - 1</c>）已经保证了它排在接入控制之后、命令分发之前，
    /// 因此本方法在 <see cref="ServiceCollectionExtensions.AddPulseBot"/> 里的调用位置
    /// 不影响正确性，放在末尾只是为了让阅读顺序与管道顺序一致。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPulseBotSessions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SessionOptions>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();

        // 等待回答的消息优先于命令被处理，理由见 SessionPromptMiddleware 的说明。
        services.AddEventMiddleware<SessionPromptMiddleware>();

        return services;
    }
}
