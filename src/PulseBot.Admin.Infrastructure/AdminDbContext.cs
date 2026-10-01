using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PulseBot.Admin.Infrastructure.Entities;

namespace PulseBot.Admin.Infrastructure;

/// <summary>
/// 后台管理的数据上下文。
/// </summary>
/// <remarks>
/// <para>
/// 只承载**后台自身**的数据（账号、刷新令牌、审计日志）。机器人的业务数据
/// （会话、权限授权、任务执行历史）仍在 Core 的内存实现里——把它们落库属于后续步骤，
/// 届时对应的 Core 存储接口会各自获得 EF Core 实现，而不是都塞进这个上下文。
/// </para>
/// <para>
/// 用 <c>IDbContextFactory</c> 而非直接注入上下文实例：后台服务多为单例
/// （审计写入会被单例的认证服务调用），而 <see cref="DbContext"/> 不是线程安全的，
/// 必须按操作创建、用完即弃。
/// </para>
/// </remarks>
public sealed class AdminDbContext : DbContext
{
    /// <summary>
    /// 时刻的统一存储转换器：<see cref="DateTimeOffset"/> ↔ UTC Unix 毫秒整数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite 没有原生时间类型。EF Core 默认把 <see cref="DateTimeOffset"/> 存成带偏移量的文本，
    /// 并且**不支持在 SQL 层对它排序或比较**——哪怕只是「按时间倒序取最近 N 条审计日志」，
    /// 也会在查询翻译阶段抛 <c>NotSupportedException</c>。
    /// </para>
    /// <para>
    /// 改存 Unix 毫秒后，排序与比较都退化为整数运算，SQL 层完全支持，时间列上的索引也照常可用。
    /// 代价是不再保留原始偏移量；本项目所有时刻都来自 <c>TimeProvider.GetUtcNow()</c>，
    /// 偏移量恒为 0，因此没有实际损失。
    /// </para>
    /// <para>
    /// 空值由 EF Core 直接透传，不会进入这里；读出时一律按 UTC 还原
    /// （SQLite 不保存 <c>DateTimeKind</c>，不从存储层推断时区）。
    /// </para>
    /// </remarks>
    private static readonly ValueConverter<DateTimeOffset, long> UtcInstantConverter = new(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));

    /// <summary>构造函数注入，由 <c>IDbContextFactory</c> 调用。</summary>
    /// <param name="options">上下文选项（连接串等）。</param>
    public AdminDbContext(DbContextOptions<AdminDbContext> options)
        : base(options)
    {
    }

    /// <summary>管理员账号。</summary>
    public DbSet<AdminUser> Users => Set<AdminUser>();

    /// <summary>刷新令牌。</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>审计日志。</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>权限名单（后台可改、内核立即生效）。</summary>
    public DbSet<PermissionListEntry> PermissionLists => Set<PermissionListEntry>();

    /// <summary>被停用的插件（有行即停用）。</summary>
    public DbSet<DisabledPlugin> DisabledPlugins => Set<DisabledPlugin>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AdminUser>(entity =>
        {
            // 登录名唯一：约束建在数据库上，而不是只在应用层判断，避免并发注册出重名账号。
            entity.HasIndex(user => user.UserName).IsUnique();
            entity.Property(user => user.UserName).HasMaxLength(64).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(256).IsRequired();
            entity.Property(user => user.Role).HasMaxLength(32).IsRequired();

            entity.Property(user => user.CreatedAt).HasConversion(UtcInstantConverter);
            entity.Property(user => user.LastLoginAt).HasConversion(UtcInstantConverter);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            // 令牌按哈希查找，唯一索引同时承担查找与防重复两个职责。
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();

            // 令牌的有效期判断（过期、撤销）都要下推到 SQL，时间列必须可比较。
            entity.Property(token => token.CreatedAt).HasConversion(UtcInstantConverter);
            entity.Property(token => token.ExpiresAt).HasConversion(UtcInstantConverter);
            entity.Property(token => token.RevokedAt).HasConversion(UtcInstantConverter);

            // 账号删除时级联清理它的令牌；此处不建导航属性，
            // 后台不需要从令牌反查账号对象，按主键查询即可。
            entity
                .HasOne<AdminUser>()
                .WithMany()
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            // 审计列表按时间倒序翻页，索引直接建在时间列上。
            entity.HasIndex(log => log.OccurredAt);
            entity.Property(log => log.OccurredAt).HasConversion(UtcInstantConverter);
            entity.Property(log => log.Action).HasMaxLength(64).IsRequired();
            entity.Property(log => log.ActorName).HasMaxLength(64);
            entity.Property(log => log.TargetType).HasMaxLength(64);
            entity.Property(log => log.TargetId).HasMaxLength(128);
            entity.Property(log => log.RemoteIp).HasMaxLength(64);
        });

        modelBuilder.Entity<PermissionListEntry>(entity =>
        {
            // 名单标识是人为给定的稳定字符串（如 super-admins），直接用作主键。
            entity.HasKey(entry => entry.Key);
            entity.Property(entry => entry.Key).HasMaxLength(64);
            entity.Property(entry => entry.UpdatedAt).HasConversion(UtcInstantConverter);
        });

        modelBuilder.Entity<DisabledPlugin>(entity =>
        {
            entity.HasKey(entry => entry.PluginTypeName);
            entity.Property(entry => entry.PluginTypeName).HasMaxLength(256);
            entity.Property(entry => entry.UpdatedAt).HasConversion(UtcInstantConverter);
        });
    }
}
