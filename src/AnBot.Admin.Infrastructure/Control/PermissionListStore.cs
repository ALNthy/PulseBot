using Microsoft.EntityFrameworkCore;
using AnBot.Admin.Infrastructure.Entities;

namespace AnBot.Admin.Infrastructure.Control;

/// <summary>权限名单在数据库里的标识，同时也是后台接口的路径参数取值。</summary>
/// <remarks>
/// 这些字符串是稳定的对外契约（前端与文档都按它们引用），改名等同于破坏兼容性。
/// </remarks>
public static class PermissionListKeys
{
    /// <summary>超级管理员名单。</summary>
    public const string SuperAdmins = "super-admins";

    /// <summary>用户黑名单。</summary>
    public const string UserBlacklist = "user-blacklist";

    /// <summary>用户白名单。</summary>
    public const string UserWhitelist = "user-whitelist";

    /// <summary>群黑名单。</summary>
    public const string GroupBlacklist = "group-blacklist";

    /// <summary>群白名单。</summary>
    public const string GroupWhitelist = "group-whitelist";

    /// <summary>全部合法标识；顺序即后台展示顺序，与内核侧快照的字段顺序一致。</summary>
    public static IReadOnlyList<string> All { get; } =
        [SuperAdmins, UserBlacklist, UserWhitelist, GroupBlacklist, GroupWhitelist];

    /// <summary>判断标识是否合法。</summary>
    public static bool IsKnown(string key) => All.Contains(key, StringComparer.Ordinal);
}

/// <summary>权限名单的持久化读写。</summary>
/// <remarks>
/// 只管「存」与「取」，不负责把结果推给内核——那是控制链路的职责（见后续的服务层），
/// 存储层不应该知道内核里有一个待更新的策略快照。
/// </remarks>
public interface IPermissionListStore
{
    /// <summary>读取全部已持久化的名单。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>名单标识 → 条目；**库里没有的名单不会出现在结果里**，调用方据此判断是否需要播种。</returns>
    ValueTask<IReadOnlyDictionary<string, string[]>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>写入一份名单，已存在则覆盖。</summary>
    /// <param name="key">名单标识，取值见 <see cref="PermissionListKeys"/>。</param>
    /// <param name="values">名单条目（原样保存，归一化由内核侧负责）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask UpsertAsync(string key, IReadOnlyList<string> values, CancellationToken cancellationToken);
}

/// <summary>基于 EF Core 的权限名单存储。</summary>
public sealed class PermissionListStore : IPermissionListStore
{
    private readonly IDbContextFactory<AdminDbContext> _dbFactory;
    private readonly TimeProvider _time;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="time">时间源（用于记录修改时刻）。</param>
    public PermissionListStore(IDbContextFactory<AdminDbContext> dbFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(time);

        _dbFactory = dbFactory;
        _time = time;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string[]>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        List<PermissionListEntry> entries = await db.PermissionLists
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entries.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Values,
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async ValueTask UpsertAsync(
        string key,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(values);

        // 物化成自己的数组，避免调用方随后的修改影响到已入队的实体。
        string[] snapshot = [.. values];

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        PermissionListEntry? entry = await db.PermissionLists
            .FirstOrDefaultAsync(candidate => candidate.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            db.PermissionLists.Add(new PermissionListEntry
            {
                Key = key,
                Values = snapshot,
                UpdatedAt = _time.GetUtcNow(),
            });
        }
        else
        {
            entry.Values = snapshot;
            entry.UpdatedAt = _time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
