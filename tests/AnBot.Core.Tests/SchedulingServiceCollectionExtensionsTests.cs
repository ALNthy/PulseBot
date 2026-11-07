using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AnBot.Core.Scheduling;

namespace AnBot.Core.Tests;

/// <summary>
/// 定时任务域的装配测试：默认实现就位、重复登记去重、宿主可整体替换。
/// </summary>
[Trait("Category", "Unit")]
public sealed class SchedulingServiceCollectionExtensionsTests
{
    /// <summary>单独调用调度扩展也应自足——不依赖 <c>AddAnBot</c> 的其余部分。</summary>
    [Fact]
    public void 单独调用调度扩展也能解析出全部服务()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<Scheduler>());
        Assert.IsType<InMemoryScheduledTaskHistoryStore>(
            provider.GetRequiredService<IScheduledTaskHistoryStore>());
        Assert.IsType<InMemoryDistributedLock>(
            provider.GetRequiredService<IDistributedLock>());
    }

    /// <summary>同一个任务类型登记两次只会被调度一次——否则任务会以两份独立状态各跑各的。</summary>
    [Fact]
    public void 重复登记同一个任务类型只调度一次()
    {
        using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddScheduledTask<StubTask>();
            services.AddScheduledTask<StubTask>();
        });

        Assert.Single(provider.GetRequiredService<IEnumerable<IScheduledTask>>());
    }

    /// <summary>任务的依赖由登记时的回调一并注册，任务构造函数可直接注入。</summary>
    [Fact]
    public void 任务自有服务通过注册回调注入()
    {
        using ServiceProvider provider = BuildProvider(services =>
            services.AddScheduledTask<DependentTask>(s => s.AddSingleton(new Marker("已注入"))));

        var task = (DependentTask)provider.GetRequiredService<IEnumerable<IScheduledTask>>().Single();

        Assert.Equal("已注入", task.Marker.Value);
    }

    /// <summary>
    /// 先注册的持久化存储与真实互斥锁会胜出：默认实现只是 <c>TryAdd</c> 的兜底，
    /// 不夺走宿主已有的实现（Admin 层的 Redis / EF Core 实现正是这样接进来的）。
    /// </summary>
    [Fact]
    public void 宿主可覆盖执行历史存储与分布式锁()
    {
        using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton<IScheduledTaskHistoryStore, CustomHistoryStore>();
            services.AddSingleton<IDistributedLock, CustomDistributedLock>();
        });

        Assert.IsType<CustomHistoryStore>(provider.GetRequiredService<IScheduledTaskHistoryStore>());
        Assert.IsType<CustomDistributedLock>(provider.GetRequiredService<IDistributedLock>());
    }

    // ---------- 测试基础设施 ----------

    /// <summary>按宿主的方式装配：日志设施用 NullLogger 顶替，先跑测试自己的注册再调用扩展。</summary>
    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        configure?.Invoke(services);
        services.AddAnBotScheduling();

        return services.BuildServiceProvider();
    }

    /// <summary>最小可用的任务替身，供装配测试使用。</summary>
    private sealed class StubTask : IScheduledTask
    {
        public ScheduledTaskSchedule Schedule { get; } = ScheduledTaskSchedule.Every(TimeSpan.FromHours(1));

        public ValueTask ExecuteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>有依赖的任务替身：验证登记回调里的注册确实能注入进来。</summary>
    private sealed class DependentTask(Marker marker) : IScheduledTask
    {
        public Marker Marker { get; } = marker;

        public ScheduledTaskSchedule Schedule { get; } = ScheduledTaskSchedule.Every(TimeSpan.FromHours(1));

        public ValueTask ExecuteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>用于辨认注入来源的标记对象。</summary>
    private sealed class Marker(string value)
    {
        public string Value { get; } = value;
    }

    /// <summary>持久化历史存储的替身（本测试只关心它是否被容器采用）。</summary>
    private sealed class CustomHistoryStore : IScheduledTaskHistoryStore
    {
        public ValueTask RecordAsync(ScheduledTaskExecution execution, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<ScheduledTaskExecution>> GetRecentAsync(
            int maxCount,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<ScheduledTaskExecution>>([]);
    }

    /// <summary>真实互斥锁的替身（本测试只关心它是否被容器采用）。</summary>
    private sealed class CustomDistributedLock : IDistributedLock
    {
        public ValueTask<IAsyncDisposable?> TryAcquireAsync(
            string key,
            TimeSpan lease,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IAsyncDisposable?>(null);
    }
}
