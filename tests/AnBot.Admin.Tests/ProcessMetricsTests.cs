using AnBot.Admin.Infrastructure.Management;

namespace AnBot.Admin.Tests;

/// <summary>进程 CPU/内存指标采样器。</summary>
[Trait("Category", "Unit")]
public sealed class ProcessMetricsTests
{
    /// <summary>当前进程的工作集必须为正，托管堆不得为负。</summary>
    [Fact]
    public void GetMemory_ReturnsPositiveWorkingSet()
    {
        var metrics = new ProcessMetrics(TimeProvider.System);

        ProcessMemory memory = metrics.GetMemory();

        Assert.True(memory.WorkingSetBytes > 0, "工作集应大于 0。");
        Assert.True(memory.ManagedBytes >= 0, "托管堆占用不应为负。");
    }

    /// <summary>
    /// 首次采样没有基线，必须返回 0；之后的比例由「CPU 时间差 ÷ 墙钟时间差」得出，
    /// 无论负载如何都应落在 0–100。
    /// </summary>
    [Fact]
    public void GetCpuUsagePercent_FirstSampleIsZero_ThenWithinRange()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ProcessMetrics(time);

        Assert.Equal(0, metrics.GetCpuUsagePercent());

        time.Advance(TimeSpan.FromSeconds(1));

        double value = metrics.GetCpuUsagePercent();

        Assert.InRange(value, 0, 100);
    }
}
