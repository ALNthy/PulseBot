using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Tests;

/// <summary>
/// 数据库 schema 自检测试：库与当前模型不一致时，启动期就报出可照做的错误，
/// 而不是等到第一次访问新功能才炸出 <c>no such table</c>。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminSchemaGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>schema 与模型一致时自检通过，启动流程照常走完。</summary>
    [Fact]
    public async Task schema一致时启动成功()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();

        await NewInitializer(database).StartAsync(CancellationToken.None);
    }

    /// <summary>库里缺少当前模型需要的表时，启动期抛出异常。</summary>
    [Fact]
    public async Task 缺表时启动失败()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();

        // 模拟「库文件建于新增该表之前」：EnsureCreated 不会给已存在的库补建新表。
        await DropTableAsync(database, "DisabledPlugins");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewInitializer(database).StartAsync(CancellationToken.None));
    }

    /// <summary>异常消息里点名缺了哪张表，并给出「删库重建」这条可执行的处理办法。</summary>
    [Fact]
    public async Task 异常消息指出缺失的表与处理办法()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();

        await DropTableAsync(database, "PermissionLists");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewInitializer(database).StartAsync(CancellationToken.None));

        Assert.Contains("PermissionLists", exception.Message, StringComparison.Ordinal);
        Assert.Contains("删除该数据库文件", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>后台关闭时不建库也不校验——此时没有任何后台数据会被访问。</summary>
    [Fact]
    public async Task 后台关闭时不校验()
    {
        await using AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
        await DropTableAsync(database, "DisabledPlugins");

        AdminOptions options = NewOptions();
        options.Enabled = false;

        await NewInitializer(database, options).StartAsync(CancellationToken.None);
    }

    // ---------- 测试基础设施 ----------

    private static async Task DropTableAsync(AdminTestDatabase database, string tableName)
    {
        await using AdminDbContext db = database.Factory.CreateDbContext();

        // 走原生命令而不是 EF 的 ExecuteSqlRaw：表名无法参数化，EF 的 SQL 注入分析器
        // 会对拼接/插值一律告警，而这里表名只来自本文件的字面量，不存在注入面。
        await db.Database.OpenConnectionAsync();
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "DROP TABLE " + tableName;

        await command.ExecuteNonQueryAsync();
    }

    private static AdminOptions NewOptions() => new()
    {
        Enabled = true,
        DatabasePath = "test-admin.db",
        Auth = new AdminAuthOptions { SigningKey = "unit-test-signing-key-0123456789abcdef" },
    };

    private static AdminDatabaseInitializer NewInitializer(
        AdminTestDatabase database,
        AdminOptions? options = null) =>
        new(
            database.Factory,
            new Pbkdf2PasswordHasher(),
            Options.Create(options ?? NewOptions()),
            new MutableTimeProvider(Start),
            NullLogger<AdminDatabaseInitializer>.Instance);
}
