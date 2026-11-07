using Microsoft.EntityFrameworkCore;

namespace AnBot.Admin.Infrastructure;

/// <summary>
/// 启动期校验数据库 schema 与当前模型一致，把「运行期才炸的 <c>no such table</c>」
/// 提前成一条照着做就能解决的启动期错误。
/// </summary>
/// <remarks>
/// <para>
/// 背景：本项目用 <c>EnsureCreated</c> 建库，它**只建缺失的库**——库文件已存在时它什么都不做，
/// 既不会补建新表，也没有迁移历史可供演进。因此新增表之后，沿用旧库文件的部署会在
/// 第一次访问新功能时报 <c>no such table</c>，而那时错误与「哪一步出了问题」已经离得很远。
/// </para>
/// <para>
/// 本校验**不自动修改数据库**：删库是不可逆的数据丢失，必须由人来决定。
/// </para>
/// </remarks>
internal static class AdminSchemaGuard
{
    /// <summary>比对模型期望的表与库中实际存在的表，缺表则抛出带操作指引的异常。</summary>
    /// <param name="db">已打开的数据上下文。</param>
    /// <param name="databasePath">数据库文件路径（用于错误提示）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">库中缺少当前模型需要的表。</exception>
    public static async ValueTask VerifyAsync(
        AdminDbContext db,
        string databasePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string[] expected =
        [
            .. db.Model
                .GetEntityTypes()
                .Select(static entityType => entityType.GetTableName())
                .OfType<string>(),
        ];

        // 对基元类型的 SqlQuery，EF Core 要求列别名为 Value；SQLite 的表清单在 sqlite_master。
        List<string> actual = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string[] missing = [.. expected.Except(actual, StringComparer.OrdinalIgnoreCase)];
        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"后台数据库缺少 {missing.Length} 张当前版本需要的表：{string.Join("、", missing)}。"
            + $"数据库文件 {databasePath} 建于当前版本之前，而本项目用 EnsureCreated 建库，"
            + "它不会为已存在的库补建新表。"
            + "处理办法：停止宿主，删除该数据库文件后重启——引导账号会按配置重新创建，"
            + "已有的名单与插件启停状态需要重新配置。");
    }
}
