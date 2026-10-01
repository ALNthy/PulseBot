using Microsoft.EntityFrameworkCore;
using PulseBot.Admin.Infrastructure.Entities;

namespace PulseBot.Admin.Infrastructure.Control;

/// <summary>插件启用状态的持久化读写。</summary>
/// <remarks>
/// 「有行即停用」：默认全部启用，因此启用操作是删除记录而不是写 <c>false</c>。
/// 落库的是插件类型全名，与内核侧命令注册使用的键完全同口径。
/// </remarks>
public interface IPluginStateStore
{
    /// <summary>读取全部被停用的插件类型全名。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<string>> GetDisabledAsync(CancellationToken cancellationToken);

    /// <summary>写入单个插件的启用状态；启用即删除记录。</summary>
    /// <param name="pluginTypeName">插件类型全名。</param>
    /// <param name="enabled"><c>true</c> 为启用，<c>false</c> 为停用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask SetEnabledAsync(string pluginTypeName, bool enabled, CancellationToken cancellationToken);
}

/// <summary>基于 EF Core 的插件启用状态存储。</summary>
public sealed class PluginStateStore : IPluginStateStore
{
    private readonly IDbContextFactory<AdminDbContext> _dbFactory;
    private readonly TimeProvider _time;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="time">时间源（用于记录修改时刻）。</param>
    public PluginStateStore(IDbContextFactory<AdminDbContext> dbFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(time);

        _dbFactory = dbFactory;
        _time = time;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetDisabledAsync(CancellationToken cancellationToken)
    {
        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await db.DisabledPlugins
            .AsNoTracking()
            .Select(static entry => entry.PluginTypeName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetEnabledAsync(
        string pluginTypeName,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        DisabledPlugin? entry = await db.DisabledPlugins
            .FirstOrDefaultAsync(candidate => candidate.PluginTypeName == pluginTypeName, cancellationToken)
            .ConfigureAwait(false);

        if (enabled)
        {
            // 启用 = 没有必要存在的记录，删掉即可；本来就没有则什么都不做。
            if (entry is not null)
            {
                db.DisabledPlugins.Remove(entry);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (entry is null)
        {
            db.DisabledPlugins.Add(new DisabledPlugin
            {
                PluginTypeName = pluginTypeName,
                UpdatedAt = _time.GetUtcNow(),
            });
        }
        else
        {
            // 重复停用：只刷新时刻，语义上是「管理员再次确认了这个状态」。
            entry.UpdatedAt = _time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
