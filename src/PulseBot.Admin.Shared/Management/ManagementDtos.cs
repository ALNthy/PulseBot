namespace PulseBot.Admin.Shared.Management;

/// <summary>
/// 宿主运行状态概览。
/// </summary>
/// <param name="StartedAt">后台服务启动时刻（近似为宿主启动时刻）。</param>
/// <param name="Uptime">已运行时长。</param>
/// <param name="ProcessId">进程 id。</param>
/// <param name="MachineName">机器名。</param>
/// <param name="RuntimeVersion">.NET 运行时版本。</param>
/// <param name="AdapterCount">已注册的适配器数量。</param>
/// <param name="PluginCount">已加载的插件数量。</param>
/// <param name="CommandCount">可触发的命令名数量（含别名）。</param>
/// <param name="ScheduledTaskCount">定时任务数量。</param>
public sealed record HostStatusInfo(
    DateTimeOffset StartedAt,
    TimeSpan Uptime,
    int ProcessId,
    string MachineName,
    string RuntimeVersion,
    int AdapterCount,
    int PluginCount,
    int CommandCount,
    int ScheduledTaskCount);

/// <summary>一个已注册的适配器。</summary>
/// <param name="Name">适配器实例名（如 <c>onebot-main</c>）。</param>
/// <param name="Platform">平台名，与 <c>ConversationId.Platform</c> 一致。</param>
public sealed record AdapterInfo(string Name, string Platform);

/// <summary>一个已加载的插件。</summary>
/// <param name="TypeName">
/// 插件实现类型的全名（无命名空间时回退为短名）。
/// 与命令表里 <see cref="CommandInfo.PluginName"/> 的取值口径一致，两者可直接关联。
/// </param>
/// <param name="Name">显示名：取自 <c>[PluginMetadata]</c>，未标注时回退为类型全名。</param>
/// <param name="Version">插件版本。</param>
/// <param name="Description">插件描述。</param>
/// <param name="Dependencies">声明的依赖插件名。</param>
/// <param name="Commands">本插件注册的命令主名（别名已归并）。</param>
public sealed record PluginInfo(
    string TypeName,
    string Name,
    string Version,
    string? Description,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Commands);

/// <summary>一个可触发的命令名。</summary>
/// <param name="Name">当前可触发的名字（主名或别名）。</param>
/// <param name="PrimaryName">声明时的主命令名。</param>
/// <param name="PluginName">注册该命令的插件类型短名。</param>
/// <param name="Description">命令说明。</param>
/// <param name="Priority">同名冲突时的裁决优先级。</param>
public sealed record CommandInfo(
    string Name,
    string PrimaryName,
    string PluginName,
    string? Description,
    int Priority);

/// <summary>一个定时任务。</summary>
/// <param name="TaskId">任务标识（任务类型全名）。</param>
/// <param name="ScheduleKind">触发方式，取值 <c>interval</c> 或 <c>cron</c>。</param>
/// <param name="Interval">固定间隔任务的间隔；Cron 任务为 <c>null</c>。</param>
/// <param name="CronExpression">Cron 任务的表达式；间隔任务为 <c>null</c>。</param>
/// <param name="TimeZoneId">Cron 任务的时区标识；间隔任务为 <c>null</c>。</param>
/// <param name="NextDueAt">下一次计划触发时刻；已停摆的任务为 <see cref="DateTimeOffset.MaxValue"/>。</param>
public sealed record ScheduledTaskInfo(
    string TaskId,
    string ScheduleKind,
    TimeSpan? Interval,
    string? CronExpression,
    string? TimeZoneId,
    DateTimeOffset NextDueAt);

/// <summary>一条定时任务执行记录。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="ScheduledAt">计划触发时刻。</param>
/// <param name="StartedAt">实际开始执行的时刻。</param>
/// <param name="Duration">本次执行耗时。</param>
/// <param name="Outcome">执行结局，取值 <c>succeeded</c>、<c>failed</c> 或 <c>canceled</c>。</param>
/// <param name="Error">失败时的异常信息；成功与取消时为 <c>null</c>。</param>
/// <remarks>
/// <see cref="Outcome"/> 刻意用字符串而非枚举：JSON 契约里是稳定的小写字面量，
/// 前端零转换即可直接展示，也不会因为枚举成员增删而改变线上契约。
/// </remarks>
public sealed record ScheduledTaskExecutionInfo(
    string TaskId,
    DateTimeOffset ScheduledAt,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Outcome,
    string? Error);
