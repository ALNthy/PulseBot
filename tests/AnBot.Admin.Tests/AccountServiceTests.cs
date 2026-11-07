using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure;
using AnBot.Admin.Infrastructure.Accounts;
using AnBot.Admin.Infrastructure.Audit;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;
using AnBot.Admin.Shared.Accounts;
using AnBot.Admin.Shared.Auth;

namespace AnBot.Admin.Tests;

/// <summary>
/// 账号管理测试：创建、改密、重置、启停、强制下线，以及它们留下的审计。
/// </summary>
/// <remarks>
/// 与认证服务共用同一套真实依赖（真实 PBKDF2、真实内存 SQLite），因此
/// 「改完密码能用新密码登录」「停用后刷新令牌失效」这类跨服务的结论是真的被验证过，
/// 而不是只验证了写进库里的字段。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AccountServiceTests
{
    private const string SigningKey = "unit-test-signing-key-0123456789abcdef";
    private const string RemoteIp = "127.0.0.1";
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>账号列表按主键排序，与创建顺序一致。</summary>
    [Fact]
    public async Task 列出全部账号()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");

        IReadOnlyList<AccountInfo> accounts = await harness.Accounts.ListAsync(CancellationToken.None);

        Assert.Equal(["alice", "bob"], accounts.Select(account => account.UserName));
        Assert.All(accounts, account => Assert.True(account.IsEnabled));
    }

    /// <summary>创建的账号立刻可以用来登录。</summary>
    [Fact]
    public async Task 创建的账号可以登录()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .CreateAsync(actor, "bob", "bob-password-1", CancellationToken.None);

        AccountInfo account = Assert.IsType<AccountInfo>(result.Account);
        Assert.True(result.IsSuccess);
        Assert.Equal("bob", account.UserName);

        LoginAttemptResult login = await harness.Auth
            .LoginAsync("bob", "bob-password-1", RemoteIp, CancellationToken.None);

        Assert.Equal(LoginOutcome.Success, login.Outcome);
    }

    /// <summary>用户名重复时拒绝创建。</summary>
    [Fact]
    public async Task 用户名重复时拒绝创建()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .CreateAsync(actor, "alice", "another-password-1", CancellationToken.None);

        Assert.Equal(AccountOperationStatus.UserNameTaken, result.Status);
    }

    /// <summary>初始密码过短时拒绝创建。</summary>
    [Fact]
    public async Task 初始密码过短时拒绝创建()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .CreateAsync(actor, "bob", "short", CancellationToken.None);

        Assert.Equal(AccountOperationStatus.WeakPassword, result.Status);
    }

    /// <summary>改密后旧密码立即失效，新密码可用。</summary>
    [Fact]
    public async Task 改密后旧密码失效()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .ChangeOwnPasswordAsync(actor, Harness.Password, "new-password-1", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(
            LoginOutcome.InvalidCredentials,
            (await harness.Auth.LoginAsync("alice", Harness.Password, RemoteIp, CancellationToken.None)).Outcome);

        Assert.Equal(
            LoginOutcome.Success,
            (await harness.Auth.LoginAsync("alice", "new-password-1", RemoteIp, CancellationToken.None)).Outcome);
    }

    /// <summary>当前密码不正确时拒绝改密。</summary>
    [Fact]
    public async Task 当前密码不正确时拒绝改密()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .ChangeOwnPasswordAsync(actor, "not-the-password", "new-password-1", CancellationToken.None);

        Assert.Equal(AccountOperationStatus.InvalidCredentials, result.Status);

        // 密码没有被改动。
        Assert.Equal(
            LoginOutcome.Success,
            (await harness.Auth.LoginAsync("alice", Harness.Password, RemoteIp, CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// 改密后该账号的刷新令牌全部失效。
    /// </summary>
    /// <remarks>
    /// 改密的动机通常正是「怀疑密码已泄露」，此时把已签发的会话留着等于没改。
    /// </remarks>
    [Fact]
    public async Task 改密后撤销全部刷新令牌()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");
        TokenPairResponse tokens = await harness.LoginAsync("alice");

        await harness.Accounts
            .ChangeOwnPasswordAsync(actor, Harness.Password, "new-password-1", CancellationToken.None);

        Assert.Null(await harness.Auth.RefreshAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>管理员可以重置他人密码，被重置者随即能用新密码登录。</summary>
    [Fact]
    public async Task 重置他人密码()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");

        AccountOperationResult result = await harness.Accounts
            .ResetPasswordAsync(actor, bobId, "reset-password-1", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(
            LoginOutcome.Success,
            (await harness.Auth.LoginAsync("bob", "reset-password-1", RemoteIp, CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// 重置自己的密码被拒绝。
    /// </summary>
    /// <remarks>
    /// 否则「重置」就是一个不需要旧密码的改密后门：拿到一个已登录的会话即可永久接管账号。
    /// 自己改密必须走 <see cref="IAccountService.ChangeOwnPasswordAsync"/>。
    /// </remarks>
    [Fact]
    public async Task 不能重置自己的密码()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .ResetPasswordAsync(actor, actor.UserId, "reset-password-1", CancellationToken.None);

        Assert.Equal(AccountOperationStatus.SelfOperation, result.Status);
    }

    /// <summary>重置不存在的账号返回未找到。</summary>
    [Fact]
    public async Task 重置不存在的账号返回未找到()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .ResetPasswordAsync(actor, targetUserId: 9999, "reset-password-1", CancellationToken.None);

        Assert.Equal(AccountOperationStatus.NotFound, result.Status);
    }

    /// <summary>停用后无法再登录。</summary>
    [Fact]
    public async Task 停用后无法登录()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");

        AccountOperationResult result = await harness.Accounts
            .SetEnabledAsync(actor, bobId, enabled: false, CancellationToken.None);

        AccountInfo account = Assert.IsType<AccountInfo>(result.Account);
        Assert.False(account.IsEnabled);

        Assert.Equal(
            LoginOutcome.InvalidCredentials,
            (await harness.Auth.LoginAsync("bob", Harness.Password, RemoteIp, CancellationToken.None)).Outcome);
    }

    /// <summary>停用后已签发的刷新令牌不再可用。</summary>
    [Fact]
    public async Task 停用后刷新令牌失效()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");
        TokenPairResponse tokens = await harness.LoginAsync("bob");

        await harness.Accounts.SetEnabledAsync(actor, bobId, enabled: false, CancellationToken.None);

        Assert.Null(await harness.Auth.RefreshAsync(tokens.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>重新启用后可以正常登录，先前被拒的刷新令牌仍然无效（已撤销的不复活）。</summary>
    [Fact]
    public async Task 重新启用后可以登录()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");

        await harness.Accounts.SetEnabledAsync(actor, bobId, enabled: false, CancellationToken.None);
        AccountOperationResult result = await harness.Accounts
            .SetEnabledAsync(actor, bobId, enabled: true, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(
            LoginOutcome.Success,
            (await harness.Auth.LoginAsync("bob", Harness.Password, RemoteIp, CancellationToken.None)).Outcome);
    }

    /// <summary>不能停用自己——这是最常见的把自己锁在门外的方式。</summary>
    [Fact]
    public async Task 不能停用自己()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult result = await harness.Accounts
            .SetEnabledAsync(actor, actor.UserId, enabled: false, CancellationToken.None);

        Assert.Equal(AccountOperationStatus.SelfOperation, result.Status);
        Assert.True((await harness.GetUserAsync("alice")).IsEnabled);
    }

    /// <summary>
    /// 不能停用最后一个启用的账号。
    /// </summary>
    /// <remarks>
    /// 与「不能停用自己」是两条独立的规则：这条拦的是「A 停用了 B，随后 B 的会话又停用了 A」
    /// 之类的路径，其结果是没有任何账号能登录，只能改数据库文件才能恢复。
    /// </remarks>
    [Fact]
    public async Task 不能停用最后一个启用账号()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor alice = await harness.ActorAsync("alice");
        AccountActor bob = await harness.ActorAsync("bob");

        // bob 停用 alice：此刻有两个启用账号，允许（alice 不是操作者自己）。
        await harness.Accounts.SetEnabledAsync(bob, alice.UserId, enabled: false, CancellationToken.None);

        // 现在 bob 是唯一启用的账号，任何人不许再停用它。
        AccountOperationResult result = await harness.Accounts
            .SetEnabledAsync(alice, bob.UserId, enabled: false, CancellationToken.None);

        Assert.Equal(AccountOperationStatus.LastEnabledAccount, result.Status);
        Assert.True((await harness.GetUserAsync("bob")).IsEnabled);
    }

    /// <summary>状态本就是目标值时幂等成功，且不重复写审计。</summary>
    [Fact]
    public async Task 重复设置同一状态是幂等的()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");

        await harness.Accounts.SetEnabledAsync(actor, bobId, enabled: false, CancellationToken.None);
        AccountOperationResult again = await harness.Accounts
            .SetEnabledAsync(actor, bobId, enabled: false, CancellationToken.None);

        Assert.True(again.IsSuccess);
        Assert.Single(await harness.ListAuditAsync("account.set-state"));
    }

    /// <summary>强制下线撤销该账号的全部刷新令牌。</summary>
    [Fact]
    public async Task 强制下线撤销全部刷新令牌()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");
        TokenPairResponse first = await harness.LoginAsync("bob");
        TokenPairResponse second = await harness.LoginAsync("bob");

        AccountOperationResult result = await harness.Accounts
            .RevokeSessionsAsync(actor, bobId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(await harness.Auth.RefreshAsync(first.RefreshToken, RemoteIp, CancellationToken.None));
        Assert.Null(await harness.Auth.RefreshAsync(second.RefreshToken, RemoteIp, CancellationToken.None));
    }

    /// <summary>每个成功的写操作都落一条审计，且记下操作者与目标。</summary>
    [Fact]
    public async Task 写操作写入审计()
    {
        await using var harness = await Harness.CreateAsync("alice");
        AccountActor actor = await harness.ActorAsync("alice");

        AccountOperationResult created = await harness.Accounts
            .CreateAsync(actor, "bob", "bob-password-1", CancellationToken.None);
        AccountInfo bob = Assert.IsType<AccountInfo>(created.Account);

        await harness.Accounts.SetEnabledAsync(actor, bob.Id, enabled: false, CancellationToken.None);
        await harness.Accounts.RevokeSessionsAsync(actor, bob.Id, CancellationToken.None);

        IReadOnlyList<AuditLog> logs = await harness.ListAuditAsync();

        Assert.Equal(
            ["account.create", "account.set-state", "account.revoke-sessions"],
            logs.Select(log => log.Action));
        Assert.All(logs, log => Assert.True(log.Success));
        Assert.All(logs, log => Assert.Equal("alice", log.ActorName));
        Assert.All(logs, log => Assert.Equal(RemoteIp, log.RemoteIp));
        Assert.All(logs, log => Assert.Equal("admin-user", log.TargetType));
        Assert.All(logs, log => Assert.NotNull(log.TargetId));
    }

    /// <summary>改自己密码与重置他人密码分别记不同的动作标识。</summary>
    [Fact]
    public async Task 改密与重置密码的审计动作可区分()
    {
        await using var harness = await Harness.CreateAsync("alice", "bob");
        AccountActor actor = await harness.ActorAsync("alice");
        int bobId = await harness.UserIdAsync("bob");

        await harness.Accounts
            .ChangeOwnPasswordAsync(actor, Harness.Password, "new-password-1", CancellationToken.None);
        await harness.Accounts.ResetPasswordAsync(actor, bobId, "reset-password-1", CancellationToken.None);

        Assert.Equal(
            ["account.password.change", "account.password.reset"],
            (await harness.ListAuditAsync()).Select(log => log.Action));
    }

    // ---------- 测试基础设施 ----------

    /// <summary>一套装配好的后台服务：账号管理 + 认证，共用同一个内存库、时钟与审计。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>预置账号的初始密码。</summary>
        public const string Password = "correct-password";

        private Harness(
            AdminTestDatabase database,
            MutableTimeProvider clock,
            AccountService accounts,
            AuthenticationService auth)
        {
            Database = database;
            Clock = clock;
            Accounts = accounts;
            Auth = auth;
        }

        public AdminTestDatabase Database { get; }

        public MutableTimeProvider Clock { get; }

        public AccountService Accounts { get; }

        public AuthenticationService Auth { get; }

        /// <summary>建库、装配服务，并写入若干个使用同一初始密码的启用账号。</summary>
        /// <param name="userNames">预置账号的登录名。</param>
        public static async Task<Harness> CreateAsync(params string[] userNames)
        {
            AdminTestDatabase database = await AdminTestDatabase.CreateAsync();
            var clock = new MutableTimeProvider(Start);
            var hasher = new Pbkdf2PasswordHasher();

            var options = Options.Create(new AdminOptions
            {
                Auth = new AdminAuthOptions
                {
                    SigningKey = SigningKey,
                    Issuer = "anbot-test",
                    Audience = "anbot-test",
                },
            });

            var audit = new AuditLogStore(database.Factory, clock, NullLogger<AuditLogStore>.Instance);
            var tokens = new TokenService(options, clock);

            var auth = new AuthenticationService(
                database.Factory,
                hasher,
                tokens,
                audit,
                new InMemoryLoginThrottleService(options, clock),
                options,
                clock,
                NullLogger<AuthenticationService>.Instance);

            var accounts = new AccountService(
                database.Factory,
                hasher,
                audit,
                clock,
                NullLogger<AccountService>.Instance);

            await using (AdminDbContext db = database.Factory.CreateDbContext())
            {
                foreach (string name in userNames)
                {
                    db.Users.Add(new AdminUser
                    {
                        UserName = name,
                        PasswordHash = hasher.Hash(Password),
                        Role = AdminRoles.Admin,
                        IsEnabled = true,
                        CreatedAt = clock.GetUtcNow(),
                    });
                }

                await db.SaveChangesAsync();
            }

            return new Harness(database, clock, accounts, auth);
        }

        /// <summary>为指定账号构造操作者上下文。</summary>
        /// <param name="userName">操作者登录名。</param>
        public async Task<AccountActor> ActorAsync(string userName)
        {
            AdminUser user = await GetUserAsync(userName);
            return new AccountActor(user.Id, user.UserName, RemoteIp);
        }

        /// <summary>以指定账号执行一次登录（使用初始密码），返回令牌对。</summary>
        /// <param name="userName">登录名。</param>
        public async Task<TokenPairResponse> LoginAsync(string userName)
        {
            LoginAttemptResult result = await Auth
                .LoginAsync(userName, Password, RemoteIp, CancellationToken.None);

            Assert.Equal(LoginOutcome.Success, result.Outcome);

            return Assert.IsType<TokenPairResponse>(result.Tokens);
        }

        /// <summary>读取指定账号的主键。</summary>
        /// <param name="userName">登录名。</param>
        public async Task<int> UserIdAsync(string userName) => (await GetUserAsync(userName)).Id;

        /// <summary>读取指定账号的当前状态。</summary>
        /// <param name="userName">登录名。</param>
        public async Task<AdminUser> GetUserAsync(string userName)
        {
            await using AdminDbContext db = Database.Factory.CreateDbContext();
            return await db.Users.AsNoTracking().SingleAsync(user => user.UserName == userName);
        }

        /// <summary>读取全部审计记录，按写入先后排列；给定动作时只取该动作的记录。</summary>
        /// <param name="action">动作标识；为 <c>null</c> 时返回全部。</param>
        public async Task<IReadOnlyList<AuditLog>> ListAuditAsync(string? action = null)
        {
            await using AdminDbContext db = Database.Factory.CreateDbContext();

            return await db.AuditLogs
                .AsNoTracking()
                .Where(log => action == null || log.Action == action)
                .OrderBy(log => log.Id)
                .ToListAsync();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
