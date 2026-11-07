using System.Diagnostics;

namespace AnBot.Admin.Infrastructure.Management;

/// <summary>进程的内存快照。</summary>
/// <param name="WorkingSetBytes">工作集（物理内存）字节数。</param>
/// <param name="ManagedBytes">GC 托管堆字节数。</param>
public readonly record struct ProcessMemory(long WorkingSetBytes, long ManagedBytes);

/// <summary>
/// 进程级 CPU 与内存指标。
/// </summary>
/// <remarks>
/// <para>
/// CPU 使用率需要两次采样：<see cref="Process.TotalProcessorTime"/> 是累计值，
/// 单次读取无法得到占用比例。本类缓存上一次采样，调用方每次查询状态时顺带刷新，
/// 因此后台每 5 秒的状态推送正好构成一个采样周期。
/// </para>
/// <para>
/// 百分比按「全部逻辑核心的平均占用」计算（除以 <see cref="Environment.ProcessorCount"/>），
/// 取值 0–100；若按单核计算，多线程进程会超过 100，界面难以直观理解。
/// </para>
/// <para>
/// 首次调用没有历史基线，返回 0 而不是一个凭空的估计值。注册为单例，以跨请求保留采样基线。
/// </para>
/// <para>
/// 只采集<strong>当前进程</strong>的指标，不读取系统级的总内存/总 CPU：
/// 跨平台获取系统级数据的实现差异大，而后台最关心的正是宿主自身占用了多少。
/// </para>
/// </remarks>
public sealed class ProcessMetrics
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private DateTimeOffset? _lastSampleAt;
    private TimeSpan _lastCpuTime;
    private double _lastCpuPercent;

    /// <summary>构造函数注入时间源（便于测试替换）。</summary>
    /// <param name="time">时间源。</param>
    public ProcessMetrics(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>计算距上次采样以来的 CPU 使用率（0–100）。</summary>
    public double GetCpuUsagePercent()
    {
        lock (_gate)
        {
            using Process process = Process.GetCurrentProcess();
            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan cpuTime = process.TotalProcessorTime;

            if (_lastSampleAt is null)
            {
                _lastSampleAt = now;
                _lastCpuTime = cpuTime;
                return 0;
            }

            double elapsedMs = (now - _lastSampleAt.Value).TotalMilliseconds;
            if (elapsedMs <= 0)
            {
                return _lastCpuPercent;
            }

            double cpuMs = (cpuTime - _lastCpuTime).TotalMilliseconds;

            _lastSampleAt = now;
            _lastCpuTime = cpuTime;
            _lastCpuPercent = Math.Clamp(
                cpuMs / (elapsedMs * Environment.ProcessorCount) * 100,
                min: 0,
                max: 100);

            return _lastCpuPercent;
        }
    }

    /// <summary>读取当前进程的内存快照。</summary>
    public ProcessMemory GetMemory()
    {
        using Process process = Process.GetCurrentProcess();

        return new ProcessMemory(
            WorkingSetBytes: process.WorkingSet64,
            ManagedBytes: GC.GetTotalMemory(forceFullCollection: false));
    }
}
