using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AnBot.Core.Scheduling;

/// <summary>
/// <see cref="IScheduledTaskHistoryStore"/> 的默认实现：执行记录保存在进程内存中，进程重启即丢失。
/// </summary>
/// <remarks>
/// <para>
/// 线程安全：记录用 <see cref="ConcurrentQueue{T}"/>，调度器单线程写入，读取端的快照遍历
/// 不会与写入冲突。
/// </para>
/// <para>
/// 容量裁剪放在**写入路径**（只有调度器一个写者），因此读取路径无需任何同步或裁剪。
/// </para>
/// </remarks>
public sealed class InMemoryScheduledTaskHistoryStore : IScheduledTaskHistoryStore
{
    private readonly ConcurrentQueue<ScheduledTaskExecution> _executions = new();
    private readonly int _capacity;

    /// <summary>创建内存历史存储。</summary>
    /// <param name="options">调度配置（历史容量）。</param>
    public InMemoryScheduledTaskHistoryStore(IOptions<SchedulingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _capacity = options.Value.HistoryCapacity;
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(ScheduledTaskExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        // 容量为非正值 = 不留历史：连入队都省掉。
        if (_capacity <= 0)
        {
            return ValueTask.CompletedTask;
        }

        _executions.Enqueue(execution);

        // 超出容量的部分从队首丢弃（最早的记录）。
        while (_executions.Count > _capacity && _executions.TryDequeue(out _))
        {
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ScheduledTaskExecution>> GetRecentAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 快照遍历：与并发写入不冲突。最新入队的在末尾，因此逆序取。
        ScheduledTaskExecution[] all = _executions.ToArray();
        var take = maxCount <= 0 ? all.Length : Math.Min(maxCount, all.Length);

        var result = new ScheduledTaskExecution[take];
        for (var i = 0; i < take; i++)
        {
            result[i] = all[all.Length - 1 - i];
        }

        return ValueTask.FromResult<IReadOnlyList<ScheduledTaskExecution>>(result);
    }
}
