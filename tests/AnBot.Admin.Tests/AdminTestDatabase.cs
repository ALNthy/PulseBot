using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AnBot.Admin.Infrastructure;

namespace AnBot.Admin.Tests;

/// <summary>
/// 一个建立在内存 SQLite 上的空库，供认证与审计测试使用。
/// </summary>
/// <remarks>
/// <para>
/// 数据库是 <c>:memory:</c> 的：它随连接存在，连接关闭即消失。因此每个测试各持有一个
/// 独立的库，互不干扰，也不会在磁盘上留下文件。
/// </para>
/// <para>
/// 用真实的 SQLite 而非内存假仓储，是因为本层的历史缺陷全部来自「CLR 语义与 SQL 语义不一致」：
/// 例如 <see cref="DateTimeOffset"/> 在 CLR 里可比较，在 SQLite 的 SQL 层却不支持排序。
/// 假仓储绕过了查询翻译，这类问题一个也测不出来。
/// </para>
/// </remarks>
internal sealed class AdminTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private AdminTestDatabase(SqliteConnection connection, IDbContextFactory<AdminDbContext> factory)
    {
        _connection = connection;
        Factory = factory;
    }

    /// <summary>数据上下文工厂；每次调用返回新实例，语义与生产装配一致。</summary>
    public IDbContextFactory<AdminDbContext> Factory { get; }

    /// <summary>建好 schema 的空库。</summary>
    public static async Task<AdminTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AdminDbContext>()
            .UseSqlite(connection)
            .Options;

        var database = new AdminTestDatabase(connection, new DirectDbContextFactory(options));

        await using (AdminDbContext context = database.Factory.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        return database;
    }

    /// <summary>关闭连接，库随之销毁。</summary>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>每次返回一个新的上下文——<see cref="DbContext"/> 不是线程安全的，必须按操作创建。</summary>
    private sealed class DirectDbContextFactory : IDbContextFactory<AdminDbContext>
    {
        private readonly DbContextOptions<AdminDbContext> _options;

        public DirectDbContextFactory(DbContextOptions<AdminDbContext> options) => _options = options;

        public AdminDbContext CreateDbContext() => new(_options);
    }
}
