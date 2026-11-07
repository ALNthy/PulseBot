using System.Reflection;
using AnBot.Admin.Shared.Management;
using AnBot.Core.Adapters;
using AnBot.Core.Commands;
using AnBot.Core.Plugins;
using AnBot.Core.Scheduling;

namespace AnBot.Admin.Infrastructure.Management;

/// <summary>
/// <see cref="IManagementService"/> 的实现：把内核各子域的只读快照聚合成后台需要的形状。
/// </summary>
/// <remarks>
/// <para>
/// 数据源全部是 Core 已有的公开只读接口，本类不做缓存——后台请求量极低，每次现取
/// 保证看到的是最新状态；真正的热路径（事件分发、命令匹配）完全不经过这里。
/// </para>
/// <para>
/// 注册为单例：依赖的 <see cref="PluginManager"/>、<see cref="ICommandDispatcher"/>、
/// <see cref="Scheduler"/> 都是单例，本类自身无可变状态。
/// </para>
/// </remarks>
public sealed class ManagementService : IManagementService
{
    private readonly IReadOnlyList<IAdapter> _adapters;
    private readonly PluginManager _plugins;
    private readonly ICommandDispatcher _commands;
    private readonly Scheduler _scheduler;
    private readonly TimeProvider _time;

    /// <summary>
    /// 「宿主启动时刻」的近似值：本服务由容器首次解析时创建，那一刻即接近宿主启动完成。
    /// </summary>
    private readonly DateTimeOffset _startedAt;

    /// <summary>构造函数注入。</summary>
    /// <param name="adapters">容器中注册的全部适配器。</param>
    /// <param name="plugins">插件管理器。</param>
    /// <param name="commands">命令分发器（读取命令表快照）。</param>
    /// <param name="scheduler">定时任务调度器。</param>
    /// <param name="time">时间源。</param>
    public ManagementService(
        IEnumerable<IAdapter> adapters,
        PluginManager plugins,
        ICommandDispatcher commands,
        Scheduler scheduler,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(time);

        // 物化一次，避免每次请求都重新枚举容器。
        _adapters = adapters as IReadOnlyList<IAdapter> ?? [.. adapters];
        _plugins = plugins;
        _commands = commands;
        _scheduler = scheduler;
        _time = time;
        _startedAt = time.GetUtcNow();
    }

    /// <inheritdoc />
    public ValueTask<HostStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _time.GetUtcNow();

        var status = new HostStatusInfo(
            StartedAt: _startedAt,
            Uptime: now - _startedAt,
            ProcessId: Environment.ProcessId,
            MachineName: Environment.MachineName,
            RuntimeVersion: Environment.Version.ToString(),
            AdapterCount: _adapters.Count,
            PluginCount: _plugins.LoadedPlugins.Count,
            CommandCount: _commands.Commands.Count,
            ScheduledTaskCount: _scheduler.Tasks.Count);

        return ValueTask.FromResult(status);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AdapterInfo>> ListAdaptersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<AdapterInfo>>(
            [.. _adapters.Select(static adapter => new AdapterInfo(adapter.Name, adapter.Platform))]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CommandInfo>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 按插件、主名、触发名排序：输出稳定，前端展示与测试断言都不受字典遍历顺序影响。
        return ValueTask.FromResult<IReadOnlyList<CommandInfo>>(
            [.. _commands.Commands
                .OrderBy(static command => command.PluginName, StringComparer.Ordinal)
                .ThenBy(static command => command.PrimaryName, StringComparer.Ordinal)
                .ThenBy(static command => command.Name, StringComparer.Ordinal)
                .Select(static command => new CommandInfo(
                    command.Name,
                    command.PrimaryName,
                    command.PluginName,
                    command.Description,
                    command.Priority))]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginInfo>> ListPluginsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 命令表按插件类型全名归组；同一命令的多个别名归并到主名，每个命令只列一次。
        Dictionary<string, List<string>> commandsByPlugin = new(StringComparer.Ordinal);
        foreach (CommandDescriptor command in _commands.Commands)
        {
            if (!commandsByPlugin.TryGetValue(command.PluginName, out List<string>? names))
            {
                names = [];
                commandsByPlugin[command.PluginName] = names;
            }

            if (!names.Contains(command.PrimaryName, StringComparer.Ordinal))
            {
                names.Add(command.PrimaryName);
            }
        }

        // 遍历容器中登记的全部插件而不是「已加载」的那些：停用的插件必须仍然出现在列表里，
        // 否则管理员在后台把它停用之后，就再也没有入口把它启用了。
        // 按引用比较判断是否已加载——插件的相等语义没有约定，用默认比较器对该类型就是引用比较，
        // 这里显式写出来，避免以后有人给插件重写 Equals 时静默改变这段的含义。
        HashSet<IPlugin> loaded = new(_plugins.LoadedPlugins, ReferenceEqualityComparer.Instance);

        List<PluginInfo> plugins = [];
        foreach (IPlugin plugin in _plugins.Plugins)
        {
            Type type = plugin.GetType();
            string typeName = type.FullName ?? type.Name;

            // 反射只服务于后台诊断，不在任何热路径上；插件数量是个位数，无需缓存。
            // 显示名回退规则与 PluginManager 内部保持一致（[PluginMetadata] → 类型全名）。
            PluginMetadataAttribute? metadata = type.GetCustomAttribute<PluginMetadataAttribute>(inherit: false);

            plugins.Add(new PluginInfo(
                TypeName: typeName,
                Name: metadata?.Name ?? typeName,
                Version: metadata?.Version ?? "未知",
                Description: metadata?.Description,
                Dependencies: metadata?.Dependencies ?? [],
                Commands: commandsByPlugin.TryGetValue(typeName, out List<string>? registered)
                    ? registered
                    : [],
                IsEnabled: _plugins.IsEnabled(typeName),
                IsLoaded: loaded.Contains(plugin)));
        }

        return ValueTask.FromResult<IReadOnlyList<PluginInfo>>(plugins);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ScheduledTaskInfo>> ListScheduledTasksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<ScheduledTaskInfo> tasks = [];
        foreach (ScheduledTaskDescriptor task in _scheduler.Tasks)
        {
            // 触发方式是封闭联合，只有间隔与 Cron 两种形态；末尾分支仅为编译器的
            // 穷尽性要求兜底，实际不可达。
            var (kind, interval, expression, timeZoneId) = task.Schedule switch
            {
                ScheduledTaskSchedule.IntervalSchedule loop =>
                    ("interval", (TimeSpan?)loop.Interval, (string?)null, (string?)null),
                ScheduledTaskSchedule.CronSchedule cron =>
                    ("cron", null, cron.Expression, cron.TimeZone.Id),
                _ => ("unknown", null, null, null),
            };

            tasks.Add(new ScheduledTaskInfo(
                task.TaskId,
                kind,
                interval,
                expression,
                timeZoneId,
                task.NextDueAt));
        }

        return ValueTask.FromResult<IReadOnlyList<ScheduledTaskInfo>>(tasks);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ScheduledTaskExecutionInfo>> GetRecentTaskExecutionsAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ScheduledTaskExecution> executions = await _scheduler
            .GetRecentExecutionsAsync(maxCount, cancellationToken)
            .ConfigureAwait(false);

        return [.. executions.Select(static execution => new ScheduledTaskExecutionInfo(
            execution.TaskId,
            execution.ScheduledAt,
            execution.StartedAt,
            execution.Duration,
            DescribeOutcome(execution.Outcome),
            execution.Error))];
    }

    /// <summary>把执行结局映射为对外的稳定字面量。</summary>
    private static string DescribeOutcome(ScheduledTaskOutcome outcome) => outcome switch
    {
        ScheduledTaskOutcome.Succeeded => "succeeded",
        ScheduledTaskOutcome.Failed => "failed",
        ScheduledTaskOutcome.Canceled => "canceled",
        _ => "unknown",
    };
}
