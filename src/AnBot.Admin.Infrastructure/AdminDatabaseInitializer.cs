using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Infrastructure;

/// <summary>
/// 启动期准备后台数据库：建表，必要时创建引导账号。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 <c>EnsureCreated</c> 而不是迁移</b>：库里的数据（账号、刷新令牌、审计日志、
/// 名单与插件启停状态）都可在删库后重建或重新配置，schema 变更时删掉数据库文件即可；
/// 换来的是不引入 <c>dotnet-ef</c> 工具链与 Design 包。代价是 <c>EnsureCreated</c>
/// **不会给已存在的库补建新表**，因此建库之后立刻用 <see cref="AdminSchemaGuard"/> 比对 schema，
/// 把「运行期才炸的缺表错误」提前成一条照着做就能解决的启动期错误。
/// </para>
/// <para>
/// <b>什么时候必须换成迁移</b>：当后台数据需要跨环境长期保留（正式部署、审计合规）时，
/// 删库重建不再可接受，届时应改用迁移——注意 <c>EnsureCreated</c> 建出的库没有迁移历史表，
/// 需要先删除数据库文件，或手工补一条基线迁移记录。
/// </para>
/// <para>
/// 本服务在宿主启动阶段完成建库，之后后台接口才可能被访问。
/// </para>
/// </remarks>
public sealed class AdminDatabaseInitializer : IHostedService
{
    private readonly IDbContextFactory<AdminDbContext> _dbFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AdminOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminDatabaseInitializer> _logger;

    /// <summary>构造函数注入。</summary>
    /// <param name="dbFactory">数据上下文工厂。</param>
    /// <param name="passwordHasher">密码哈希（用于引导账号）。</param>
    /// <param name="options">后台配置。</param>
    /// <param name="time">时间源。</param>
    /// <param name="logger">结构化日志。</param>
    public AdminDatabaseInitializer(
        IDbContextFactory<AdminDbContext> dbFactory,
        IPasswordHasher passwordHasher,
        IOptions<AdminOptions> options,
        TimeProvider time,
        ILogger<AdminDatabaseInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _passwordHasher = passwordHasher;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            // 后台被关闭：不建库、不落任何文件，宿主退化成纯机器人进程。
            return;
        }

        await using AdminDbContext db = await _dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // 与连接串同一种解析方式：错误提示里的路径必须就是实际使用的文件。
        string databasePath = AdminInfrastructureServiceCollectionExtensions.ResolveDatabasePath(_options.DatabasePath);

        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // 建库之后立刻比对 schema：EnsureCreated 只建缺失的库，不会给旧库补建新表。
        await AdminSchemaGuard.VerifyAsync(db, databasePath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("后台数据库已就绪：{DatabasePath}", databasePath);

        await EnsureBootstrapUserAsync(db, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>账号表为空时创建引导账号。</summary>
    private async Task EnsureBootstrapUserAsync(AdminDbContext db, CancellationToken cancellationToken)
    {
        // 只在账号表为空时介入：一旦有了账号，配置里的引导密码就不再被读取，
        // 因此它可以（也应该）在首次登录后被清掉。
        if (await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Auth.BootstrapPassword))
        {
            _logger.LogWarning(
                "后台尚无任何账号，且未配置 {Section}:Auth:BootstrapPassword，因此没有创建引导账号——"
                + "后台会正常启动，但无人能登录。请配置该项后重启，或设 {Section}:Enabled = false 关闭后台。",
                AdminOptions.SectionName,
                AdminOptions.SectionName);

            return;
        }

        AdminUser user = new()
        {
            UserName = string.IsNullOrWhiteSpace(_options.Auth.BootstrapUser)
                ? "admin"
                : _options.Auth.BootstrapUser,
            PasswordHash = _passwordHasher.Hash(_options.Auth.BootstrapPassword),
            Role = AdminRoles.Admin,
            IsEnabled = true,
            CreatedAt = _time.GetUtcNow(),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "已创建后台引导账号 {UserName}。请登录后从配置中移除引导密码——"
            + "它只在账号表为空时被读取，但留在配置里仍是不必要的暴露面。",
            user.UserName);
    }
}
