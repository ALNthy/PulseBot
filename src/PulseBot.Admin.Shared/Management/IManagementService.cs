namespace PulseBot.Admin.Shared.Management;

/// <summary>
/// 后台管理与内核交互的唯一入口。
/// </summary>
/// <remarks>
/// <para>
/// 后台**只**通过本接口读取内核状态，不直接触碰命令分发、插件管理或调度器的内部结构；
/// 与之对应，内核不为后台做任何让步——不暴露内部服务，也不改动事件与命令的热路径。
/// </para>
/// <para>
/// 全部方法返回的是调用那一刻的状态快照，不是活引用：后台读到的数据可能在返回后立即过期。
/// 监控用途不追求精确，这与 Core 里 <c>LoadedPlugins</c> / <c>Scheduler.Tasks</c>
/// 的既定取舍一致。
/// </para>
/// <para>实现必须线程安全：HTTP 请求线程读取，内核线程在并发写入。</para>
/// </remarks>
public interface IManagementService
{
    /// <summary>宿主运行状态概览。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<HostStatusInfo> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>已注册的适配器（平台接入点）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<AdapterInfo>> ListAdaptersAsync(CancellationToken cancellationToken);

    /// <summary>已加载的插件及其元数据，附带各插件注册的命令。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<PluginInfo>> ListPluginsAsync(CancellationToken cancellationToken);

    /// <summary>当前可触发的全部命令名（主名与别名各一条）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<CommandInfo>> ListCommandsAsync(CancellationToken cancellationToken);

    /// <summary>定时任务清单。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<ScheduledTaskInfo>> ListScheduledTasksAsync(CancellationToken cancellationToken);

    /// <summary>最近若干条定时任务执行记录，最新的在前。</summary>
    /// <param name="maxCount">最多返回多少条。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<ScheduledTaskExecutionInfo>> GetRecentTaskExecutionsAsync(
        int maxCount,
        CancellationToken cancellationToken);
}
