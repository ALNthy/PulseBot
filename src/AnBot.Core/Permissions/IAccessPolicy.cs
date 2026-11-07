using Microsoft.Extensions.Options;

namespace AnBot.Core.Permissions;

/// <summary>
/// 运行时接入策略的持有者：读取当前生效的快照，并以整体替换的方式发布新快照。
/// </summary>
/// <remarks>
/// <para>
/// 本接口是「后台改名单 → 内核立即生效」这条链在 Core 侧的落点：
/// 后台把新名单落库后调用 <see cref="Update"/> 即可，不需要重启宿主，
/// 也不需要 <c>IOptionsMonitor</c> 那套配置系统改造——
/// 名单的权威来源本就该是数据库，而不是只读的配置文件。
/// </para>
/// <para>
/// 读路径（每条消息判定一次）只有一次 <c>volatile</c> 引用读，无锁、无分配；
/// 写路径是低频的管理操作，整体替换一个引用即可，不存在读写出错的内存窗口。
/// </para>
/// </remarks>
public interface IAccessPolicy
{
    /// <summary>当前生效的策略快照。可在热路径上反复读取。</summary>
    AccessPolicySnapshot Current { get; }

    /// <summary>发布一份新快照，替换当前生效的策略；此后所有判定立即使用新名单。</summary>
    /// <param name="snapshot">新快照。</param>
    void Update(AccessPolicySnapshot snapshot);
}

/// <summary>
/// <see cref="IAccessPolicy"/> 的默认实现：快照保存在进程内存中。
/// </summary>
/// <remarks>
/// 初值由 <see cref="PermissionOptions"/> 播种，因此不接后台管理时
/// 行为与「名单只在配置文件里」的过去完全一致。
/// 持久化不是本类型的职责——把快照写进数据库、以及启动时从数据库还原，
/// 都由后台管理一侧在启动阶段与变更时驱动。
/// </remarks>
public sealed class InMemoryAccessPolicy : IAccessPolicy
{
    /// <summary>
    /// 当前快照。<c>volatile</c> 保证写线程发布的引用对读线程立即可见；
    /// 引用赋值本身是原子的，因此不需要锁或 <c>Interlocked</c>。
    /// </summary>
    private volatile AccessPolicySnapshot _current;

    /// <summary>创建策略持有者，并以配置中的名单作为初值。</summary>
    /// <param name="options">权限配置（作为初始快照的播种来源）。</param>
    public InMemoryAccessPolicy(IOptions<PermissionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _current = AccessPolicySnapshot.From(options.Value);
    }

    /// <inheritdoc />
    public AccessPolicySnapshot Current => _current;

    /// <inheritdoc />
    public void Update(AccessPolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _current = snapshot;
    }
}
