using Microsoft.Extensions.Logging.Abstractions;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Shared.Audit;

namespace AnBot.Admin.Tests;

/// <summary>
/// 审计日志存储测试：字段落库、时间倒序读取，以及写库失败时的降级行为。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AuditLogStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>按时间倒序返回，最新的在前。</summary>
    /// <remarks>
    /// 这是针对 SQLite + <see cref="DateTimeOffset"/> 的回归测试：EF Core 默认把
    /// <c>DateTimeOffset</c> 存成文本，而 SQLite 在 SQL 层不支持对它排序，查询会在翻译阶段
    /// 直接抛 <c>NotSupportedException</c>。若有人把 <c>AdminDbContext</c> 里的
    /// 值转换器去掉，本用例会立刻失败。
    /// </remarks>
    [Fact]
    public async Task 读取时按时间倒序返回()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(Start);
        AuditLogStore store = NewStore(database, clock);

        await store.RecordAsync(new AuditEntry("first"), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(new AuditEntry("second"), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(new AuditEntry("third"), CancellationToken.None);

        IReadOnlyList<AuditLogInfo> logs = await store.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal(["third", "second", "first"], logs.Select(log => log.Action));
    }

    /// <summary>同一时刻写入的多条记录按自增主键兜底，顺序稳定且仍是倒序。</summary>
    [Fact]
    public async Task 同一时刻的记录按主键倒序()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        AuditLogStore store = NewStore(database, new MutableTimeProvider(Start));

        await store.RecordAsync(new AuditEntry("older"), CancellationToken.None);
        await store.RecordAsync(new AuditEntry("newer"), CancellationToken.None);

        IReadOnlyList<AuditLogInfo> logs = await store.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal(["newer", "older"], logs.Select(log => log.Action));
    }

    /// <summary>返回条数受请求值约束，且截取的是最新的若干条。</summary>
    [Fact]
    public async Task 返回条数受请求上限约束()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(Start);
        AuditLogStore store = NewStore(database, clock);

        for (int i = 1; i <= 5; i++)
        {
            await store.RecordAsync(new AuditEntry($"action-{i}"), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        IReadOnlyList<AuditLogInfo> logs = await store.GetRecentAsync(2, CancellationToken.None);

        Assert.Equal(["action-5", "action-4"], logs.Select(log => log.Action));
    }

    /// <summary>非正的请求值取实现默认上限，而不是返回空集。</summary>
    [Fact]
    public async Task 非正请求值取默认上限()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        AuditLogStore store = NewStore(database, new MutableTimeProvider(Start));

        await store.RecordAsync(new AuditEntry("only"), CancellationToken.None);

        Assert.Single(await store.GetRecentAsync(0, CancellationToken.None));
        Assert.Single(await store.GetRecentAsync(-5, CancellationToken.None));
    }

    /// <summary>写入的字段完整落库，读出后逐项一致。</summary>
    [Fact]
    public async Task 写入的字段完整落库()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        AuditLogStore store = NewStore(database, new MutableTimeProvider(Start));

        await store.RecordAsync(
            new AuditEntry(
                Action: "auth.login",
                ActorName: "admin",
                TargetType: "admin-user",
                TargetId: "1",
                Detail: "账号不存在、已停用或密码错误",
                RemoteIp: "127.0.0.1",
                Success: false),
            CancellationToken.None);

        AuditLogInfo log = Assert.Single(await store.GetRecentAsync(10, CancellationToken.None));

        Assert.Equal(Start, log.OccurredAt);
        Assert.Equal("auth.login", log.Action);
        Assert.Equal("admin", log.ActorName);
        Assert.Equal("admin-user", log.TargetType);
        Assert.Equal("1", log.TargetId);
        Assert.Equal("账号不存在、已停用或密码错误", log.Detail);
        Assert.Equal("127.0.0.1", log.RemoteIp);
        Assert.False(log.Success);
    }

    /// <summary>写库失败只记录日志，不向调用方抛异常——审计写入绝不能反过来阻断业务流程。</summary>
    [Fact]
    public async Task 写库失败时不抛异常()
    {
        AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        await database.DisposeAsync();   // 连接已关闭，后续任何数据库操作都会失败

        AuditLogStore store = NewStore(database, new MutableTimeProvider(Start));

        // 不抛出即为通过：登录流程不该因为「记不上账」而失败。
        await store.RecordAsync(new AuditEntry("cannot-be-written"), CancellationToken.None);
    }

    // ---------- 测试基础设施 ----------

    private static AuditLogStore NewStore(AdminTestDatabase database, MutableTimeProvider clock)
        => new(database.Factory, clock, NullLogger<AuditLogStore>.Instance);
}
