using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AnBot.Core.Scheduling;

/// <summary>
/// 定时任务域的 DI 注册扩展。
/// </summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// 注册定时任务调度：调度器、执行历史存储、分布式锁与调度配置。
    /// 宿主仍需用 <see cref="AddScheduledTask{TTask}"/> 逐个登记任务，并自行驱动
    /// <see cref="Scheduler.RunDueTasksAsync"/>（Core 不引用 Hosting 抽象，不实现 IHostedService）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 历史存储与分布式锁都用 <c>TryAdd</c> 注册，宿主可换成持久化 / 带真实互斥的实现
    /// （<c>services.AddSingleton&lt;IScheduledTaskHistoryStore, MyStore&gt;()</c>）。
    /// </para>
    /// <para>
    /// 时间源同样用 <c>TryAdd</c>，让测试或宿主能够覆盖——否则任务到期无法在测试里稳定复现
    /// （与 <c>AddAnBotSessions</c> 的理由相同）。
    /// </para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddAnBotScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SchedulingOptions>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IScheduledTaskHistoryStore, InMemoryScheduledTaskHistoryStore>();
        services.TryAddSingleton<IDistributedLock, InMemoryDistributedLock>();
        services.TryAddSingleton<Scheduler>();

        return services;
    }

    /// <summary>
    /// 登记一个定时任务：以 <see cref="IScheduledTask"/> 单例的形式注册（构造注入可用），
    /// 由 <see cref="Scheduler"/> 统一调度。重复登记同一任务类型会被去重。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 任务实例只以 <see cref="IScheduledTask"/> 接口对外可见，不额外注册 <c>TTask</c> 自身——
    /// 双注册会创建两个任务实例，同一个任务被调度两次，且两者的状态互相分叉
    /// （与 <c>AddPlugin</c> 的告警同理）。
    /// </para>
    /// <para>
    /// 同理，**同一个类不要同时登记成 <c>IPlugin</c> 与 <c>IScheduledTask</c>**：那是两个独立单例。
    /// 需要共享状态时应当拆成两个类，让它们注入同一份领域服务。
    /// </para>
    /// </remarks>
    /// <typeparam name="TTask">任务实现类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="configureTaskServices">
    /// 任务自有服务的注册回调（推荐封装为任务项目自己的扩展方法，例如
    /// <c>services.AddScheduledTask&lt;ReportTask&gt;(s =&gt; s.AddSingleton&lt;IReportStore, ReportStore&gt;())</c>）。
    /// </param>
    public static IServiceCollection AddScheduledTask<TTask>(
        this IServiceCollection services,
        Action<IServiceCollection>? configureTaskServices = null)
        where TTask : class, IScheduledTask
    {
        ArgumentNullException.ThrowIfNull(services);

        // 任务自有服务先注册，供任务构造函数注入。
        configureTaskServices?.Invoke(services);

        // TryAddEnumerable 按 (IScheduledTask, TTask) 去重，重复登记不会让任务被调度两次。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IScheduledTask, TTask>());
        return services;
    }
}
