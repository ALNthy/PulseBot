using System.Collections.Immutable;

namespace AnBot.Core.Plugins;

/// <summary>
/// 插件启用状态的持有者：被停用的插件，其注册的命令不再可触发。
/// </summary>
/// <remarks>
/// <para>
/// 粒度为「插件类型全名」，与 <c>CommandRegistration.PluginName</c>、
/// <c>PluginInfo.TypeName</c> 同口径，因此判定只需一次字符串查找。
/// </para>
/// <para>
/// 状态由不可变集合承载、以「整体替换引用」的方式发布：读路径（每条命中命令的消息）
/// 只有一次 <c>Volatile.Read</c> 加一次哈希查找，无锁、无分配；
/// 写路径是低频管理操作，用「读取—计算—比较交换」重试应对并发，
/// 与 <see cref="Permissions.InMemoryPermissionService"/> 的授权表同一套路数。
/// </para>
/// <para>
/// <b>本类型只管状态，不管生命周期。</b>停用不会调用 <see cref="IPlugin.OnUnloadAsync"/>，
/// 插件实例、它持有的资源与它注册的事件处理器都原封不动——被停用的只是
/// 「它注册的命令还能不能被触发」。这是刻意的：Core 不假设插件可以被安全地热卸载，
/// 也不掌握「插件与事件处理器」的归属关系（两者独立注册）。
/// </para>
/// </remarks>
public sealed class PluginStateRegistry
{
    /// <summary>
    /// 被停用的插件类型全名集合。字段刻意不加 <c>volatile</c>：
    /// 写路径需要 <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 的引用比较，
    /// 而带 <c>volatile</c> 的字段不能按引用传递；读写可见性由 <see cref="Volatile"/> 保证。
    /// </summary>
    private ImmutableHashSet<string> _disabled = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>当前被停用的插件类型全名集合（供后台展示与诊断）。</summary>
    public IReadOnlyCollection<string> DisabledPlugins => Volatile.Read(ref _disabled);

    /// <summary>判断插件是否被停用；未登记过的插件视为启用。</summary>
    /// <param name="pluginTypeName">插件类型全名。</param>
    public bool IsDisabled(string pluginTypeName)
        => Volatile.Read(ref _disabled).Contains(pluginTypeName);

    /// <summary>
    /// 启用或停用单个插件。立即生效：命令分发的下一步就会按新状态判定。
    /// </summary>
    /// <param name="pluginTypeName">插件类型全名。</param>
    /// <param name="enabled"><c>true</c> 为启用，<c>false</c> 为停用。</param>
    /// <returns>状态是否真的发生变化（重复停用或重复启用返回 <c>false</c>）。</returns>
    public bool SetEnabled(string pluginTypeName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);

        while (true)
        {
            ImmutableHashSet<string> current = Volatile.Read(ref _disabled);
            ImmutableHashSet<string> updated = enabled
                ? current.Remove(pluginTypeName)
                : current.Add(pluginTypeName);

            // 集合内容未变化时 Add/Remove 返回同一实例，据此跳过无意义的写入。
            if (ReferenceEquals(updated, current))
            {
                return false;
            }

            // 引用比较即乐观锁：失败说明有并发写入，重读后重试。
            if (ReferenceEquals(Interlocked.CompareExchange(ref _disabled, updated, current), current))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 整体替换停用集合。用于启动阶段从持久化数据水合（此时尚无并发读者）。
    /// </summary>
    /// <param name="disabledPluginTypeNames">被停用的插件类型全名；空白项被忽略。</param>
    public void Replace(IEnumerable<string> disabledPluginTypeNames)
    {
        ArgumentNullException.ThrowIfNull(disabledPluginTypeNames);

        Volatile.Write(
            ref _disabled,
            disabledPluginTypeNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToImmutableHashSet(StringComparer.Ordinal));
    }
}
