namespace AnBot.Admin.Tests;

/// <summary>
/// 可手动推进的时间源。
/// </summary>
/// <remarks>
/// 「刷新令牌过期」「访问令牌超期」这类分支只有时间推移后才成立，用系统时钟既等不起、
/// 结果也不确定。被测服务全部通过 <see cref="TimeProvider"/> 取时，因此测试可以完全掌控它。
/// </remarks>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>以指定时刻起算。</summary>
    /// <param name="start">起始的 UTC 时刻。</param>
    public MutableTimeProvider(DateTimeOffset start) => _now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>把时钟向前拨。</summary>
    /// <param name="delta">推进的时长。</param>
    public void Advance(TimeSpan delta) => _now += delta;
}
