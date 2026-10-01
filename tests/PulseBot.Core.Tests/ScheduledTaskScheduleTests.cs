using PulseBot.Core.Scheduling;

namespace PulseBot.Core.Tests;

/// <summary>
/// 触发方式（<see cref="ScheduledTaskSchedule"/>）的工厂校验与形态测试。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ScheduledTaskScheduleTests
{
    /// <summary>
    /// 非正间隔在创建时就要失败：这样的任务会在每个轮询周期都判定为到期。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 间隔为非正值时抛出(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScheduledTaskSchedule.Every(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// 空白表达式在创建时失败；语法非法（如 <c>"不是表达式"</c>）则不在这里检查，
    /// 而是推迟到调度器构造期——一个表达式写错不该让机器人起不来。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Cron表达式为空白时抛出(string expression)
    {
        Assert.Throws<ArgumentException>(() => ScheduledTaskSchedule.Cron(expression));
    }

    /// <summary>不指定时区时使用本机时区——任务的「每天 8 点」通常指本地时间。</summary>
    [Fact]
    public void 未指定时区时使用本机时区()
    {
        var schedule = Assert.IsType<ScheduledTaskSchedule.CronSchedule>(
            ScheduledTaskSchedule.Cron("0 8 * * *"));

        Assert.Equal("0 8 * * *", schedule.Expression);
        Assert.Equal(TimeZoneInfo.Local, schedule.TimeZone);
    }

    /// <summary>工厂返回的具体形态即调度器注册期分派时匹配的形态。</summary>
    [Fact]
    public void 工厂返回对应的具体形态()
    {
        Assert.IsType<ScheduledTaskSchedule.IntervalSchedule>(
            ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));

        Assert.IsType<ScheduledTaskSchedule.CronSchedule>(
            ScheduledTaskSchedule.Cron("0 8 * * *", TimeZoneInfo.Utc));
    }

    /// <summary>记录类型的值语义：字段相同即相等，便于测试与去重比较。</summary>
    [Fact]
    public void 相同参数的触发方式相等()
    {
        Assert.Equal(
            ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)),
            ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)));

        Assert.Equal(
            ScheduledTaskSchedule.Cron("0 8 * * *", TimeZoneInfo.Utc),
            ScheduledTaskSchedule.Cron("0 8 * * *", TimeZoneInfo.Utc));

        Assert.NotEqual(
            ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(1)),
            ScheduledTaskSchedule.Every(TimeSpan.FromMinutes(2)));
    }
}
