using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using AnBot.Core.Events;
using AnBot.Core.Messages;

namespace AnBot.Core.Permissions;

/// <summary>
/// <see cref="IPermissionService"/> 的默认实现：授权表保存在进程内存中。
/// </summary>
/// <remarks>
/// <para>
/// 进程重启后授权丢失，宿主需在启动时重新授予（或改用持久化实现替换本类型）。
/// 后台管理接入后，由它负责把授权变更落库并通过本接口驱动运行时。
/// </para>
/// <para>
/// 线程安全：授权表用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 承载不可变集合，
/// 读路径无锁（热路径上只有哈希查找），写路径以「读取—计算—比较交换」的方式重试。
/// </para>
/// </remarks>
public sealed class InMemoryPermissionService : IPermissionService
{
    /// <summary>通配节点：拥有它即拥有全部权限。</summary>
    public const string WildcardNode = "*";

    /// <summary>授权表：主体 → 它拥有的权限节点集合（不可变，可直接无锁读取）。</summary>
    private readonly ConcurrentDictionary<PermissionSubject, ImmutableHashSet<string>> _grants = new();

    /// <summary>接入策略持有者：超管名单与拒绝提示语都取自当前快照，因而支持热更新。</summary>
    private readonly IAccessPolicy _policy;

    private readonly ILogger<InMemoryPermissionService> _logger;

    /// <summary>创建权限服务。</summary>
    /// <param name="policy">接入策略持有者（提供超级管理员名单与拒绝提示语）。</param>
    /// <param name="logger">结构化日志。</param>
    public InMemoryPermissionService(
        IAccessPolicy policy,
        ILogger<InMemoryPermissionService> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>每次读取取当前快照，因此后台更新提示语后立即生效。</remarks>
    public string DeniedMessage => _policy.Current.DeniedMessage;

    /// <inheritdoc />
    public ValueTask<bool> IsGrantedAsync(
        MessageEvent message,
        PermissionRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(requirement);

        // 未声明权限要求的命令一律放行（默认放行策略）。
        if (requirement.IsEmpty)
        {
            return ValueTask.FromResult(true);
        }

        // 超级管理员拥有全部权限，无需逐条授予。
        if (IsSuperAdmin(message.SenderId))
        {
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(Evaluate(message, requirement));
    }

    /// <inheritdoc />
    public ValueTask<bool> IsGrantedAsync(
        PermissionSubject subject,
        string node,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        if (subject.Kind == PermissionSubjectKind.User && IsSuperAdmin(subject.Id))
        {
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(HasNode(subject, node));
    }

    /// <inheritdoc />
    public ValueTask GrantAsync(PermissionSubject subject, string node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        // 不可变集合 + AddOrUpdate：并发授予不会丢失任何一条，也不需要显式加锁。
        _grants.AddOrUpdate(
            subject,
            static (_, added) => ImmutableHashSet.Create(added),
            static (_, existing, added) => existing.Add(added),
            node);

        _logger.LogInformation("已授予 {Subject} 权限节点 {Node}。", subject, node);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RevokeAsync(PermissionSubject subject, string node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        // 循环重试以应对并发修改：撤销是低频管理操作，重试的代价可以忽略。
        while (_grants.TryGetValue(subject, out var existing))
        {
            var updated = existing.Remove(node);

            // 集合中本来就没有该节点时，ImmutableHashSet.Remove 返回同一实例，据此跳过无意义的写入。
            if (ReferenceEquals(updated, existing))
            {
                return ValueTask.CompletedTask;
            }

            if (_grants.TryUpdate(subject, updated, existing))
            {
                _logger.LogInformation("已撤销 {Subject} 的权限节点 {Node}。", subject, node);
                return ValueTask.CompletedTask;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<string>> ListAsync(
        PermissionSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        IReadOnlyCollection<string> nodes = _grants.TryGetValue(subject, out var granted)
            ? granted
            : [];

        return ValueTask.FromResult(nodes);
    }

    /// <summary>按组合方式判定整条要求是否被满足。</summary>
    private bool Evaluate(MessageEvent message, PermissionRequirement requirement)
    {
        if (requirement.Mode == PermissionRequirementMode.All)
        {
            foreach (var node in requirement.Nodes)
            {
                if (!IsNodeGranted(message, node))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var node in requirement.Nodes)
        {
            if (IsNodeGranted(message, node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断某个节点在当前会话中是否被满足：用户级、群级、全局三级授权任一命中即可。
    /// </summary>
    private bool IsNodeGranted(MessageEvent message, string node)
    {
        // 用户级：权限跟随人，在该用户出现的任意会话中都生效。
        if (HasNode(PermissionSubject.User(message.SenderId), node))
        {
            return true;
        }

        // 群级：只在群/频道会话中生效；私聊没有群身份，跳过这一级。
        if (message.Conversation.Type != ConversationType.Private
            && HasNode(PermissionSubject.Group(message.Conversation.Id), node))
        {
            return true;
        }

        return HasNode(PermissionSubject.Global, node);
    }

    /// <summary>查表判断主体是否拥有该节点（含通配节点 <c>*</c>）。</summary>
    private bool HasNode(PermissionSubject subject, string node)
        => _grants.TryGetValue(subject, out var nodes)
            && (nodes.Contains(node) || nodes.Contains(WildcardNode));

    private bool IsSuperAdmin(string userId) => _policy.Current.SuperAdmins.Contains(userId);
}
