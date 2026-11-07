namespace AnBot.Core.Scheduling;

/// <summary>
/// 定时任务的触发方式：**封闭联合**，恰好是「固定间隔」或「Cron 表达式」之一。
/// </summary>
/// <remarks>
/// <para>
/// 用封闭联合而不是「两个派生接口」（<c>IIntervalTask</c> / <c>ICronTask</c>）：
/// 调度器在注册期必须对两种形态做同一次「编译」，接口继承会迫使调用方先做类型判断；
/// 封闭联合让调度器用一次 <c>switch</c> 穷举处理，且编译期不允许出现第三种形态。
/// </para>
/// <para>
/// 构造函数是 <c>private protected</c>：只有本程序集内的派生类能继承，而两个派生记录
/// 以嵌套类型的形式定义在本类内部，外部无法再添加新形态。
/// </para>
/// <para>
/// 创建实例请用工厂方法 <see cref="Every"/> 与 <see cref="Cron"/>——参数校验放在那里，
/// 让「写出非法触发方式」在启动期就暴露，而不是等到第一次调度。
/// </para>
/// </remarks>
public abstract record ScheduledTaskSchedule
{
    /// <summary>仅供嵌套派生记录调用；外部程序集无法继承本类型。</summary>
    private protected ScheduledTaskSchedule()
    {
    }

    /// <summary>固定间隔触发：每隔一段时间执行一次。</summary>
    /// <param name="interval">两次执行之间的间隔，必须为正。</param>
    /// <returns>间隔触发方式。</returns>
    /// <exception cref="ArgumentOutOfRangeException">间隔为非正值时抛出。</exception>
    /// <remarks>
    /// 间隔从**上一次执行结束**开始计算（fixed-delay），而不是从计划时刻开始（fixed-rate）：
    /// 执行耗时不会累积成越来越大的漂移，任务也不会为了追赶而排队。
    /// </remarks>
    public static ScheduledTaskSchedule Every(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "任务间隔必须为正。");
        }

        return new IntervalSchedule(interval);
    }

    /// <summary>按 Cron 表达式触发。</summary>
    /// <param name="expression">
    /// 标准 5 段（分 时 日 月 周）或 6 段（秒 分 时 日 月 周）表达式，段数由空白分隔的字段数推断。
    /// </param>
    /// <param name="timeZone">解释表达式所用的时区；传 <c>null</c> 表示本机时区。</param>
    /// <returns>Cron 触发方式。</returns>
    /// <exception cref="ArgumentException">表达式为空白时抛出。</exception>
    /// <remarks>
    /// 表达式**语法**非法时本方法不抛——语法检查发生在调度器构造期，非法表达式会被记录并跳过
    /// （见 <see cref="Scheduler"/>）：一个表达式写错不该让整个机器人起不来。
    /// </remarks>
    public static ScheduledTaskSchedule Cron(string expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        return new CronSchedule(expression, timeZone ?? TimeZoneInfo.Local);
    }

    /// <summary>固定间隔触发，间隔为 <paramref name="Interval"/>。</summary>
    /// <param name="Interval">两次执行之间的间隔。</param>
    public sealed record IntervalSchedule(TimeSpan Interval) : ScheduledTaskSchedule;

    /// <summary>按 <paramref name="Expression"/> 在 <paramref name="TimeZone"/> 时区下触发。</summary>
    /// <param name="Expression">Cron 表达式原文。</param>
    /// <param name="TimeZone">解释表达式所用的时区。</param>
    public sealed record CronSchedule(string Expression, TimeZoneInfo TimeZone) : ScheduledTaskSchedule;
}
