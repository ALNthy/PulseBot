using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PulseBot.Admin.Infrastructure.Audit;
using PulseBot.Admin.Infrastructure.Auth;
using PulseBot.Admin.Infrastructure.Management;
using PulseBot.Admin.Shared;
using PulseBot.Admin.Shared.Management;

namespace PulseBot.Admin.Infrastructure;

/// <summary>后台基础设施的依赖注入装配。</summary>
public static class AdminInfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// 注册后台基础设施：数据访问、认证与令牌、审计日志，以及
    /// <see cref="IManagementService"/> 的实现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用方需自行完成配置绑定（与其它子域扩展一致）：
    /// </para>
    /// <code>
    /// builder.Services.Configure&lt;AdminOptions&gt;(
    ///     builder.Configuration.GetSection(AdminOptions.SectionName));
    /// builder.Services.AddAdminInfrastructure();
    /// </code>
    /// <para>
    /// <see cref="AdminOptions.Enabled"/> 为 <c>false</c> 时不建库、不校验签名密钥，
    /// 但服务仍然注册——是否暴露 HTTP 端点由宿主决定（见 Admin.Api 的 <c>MapAdminApi</c>）。
    /// </para>
    /// </remarks>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddAdminInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 配置非法（如启用后台却没配签名密钥）在宿主启动期就失败，
        // 而不是等到第一次登录才发现。
        services.AddOptions<AdminOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AdminOptions>, AdminOptionsValidator>());

        services.AddDbContextFactory<AdminDbContext>((provider, builder) =>
        {
            AdminOptions options = provider.GetRequiredService<IOptions<AdminOptions>>().Value;
            builder.UseSqlite($"Data Source={ResolveDatabasePath(options.DatabasePath)}");
        });

        // 时间源与 Core 各子域一致：TryAdd 让测试或宿主能够替换成可控实现。
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<TokenService>();
        services.TryAddSingleton<IAuthenticationService, AuthenticationService>();
        services.TryAddSingleton<IAuditLogStore, AuditLogStore>();
        services.TryAddSingleton<IManagementService, ManagementService>();

        services.AddHostedService<AdminDatabaseInitializer>();

        return services;
    }

    /// <summary>把配置里的数据库路径解析为绝对路径。</summary>
    private static string ResolveDatabasePath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        // 相对路径按「应用所在目录」解析：宿主的当前工作目录会随启动方式变化
        // （从仓库根 dotnet run 与直接运行 exe 就不同），数据库文件不该因此搬来搬去。
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }
}

/// <summary>启动期校验后台配置。</summary>
/// <remarks>
/// 只校验「配错了就没法安全运行」的项，且仅在后台启用时生效。
/// 引导密码为空不算错误——它只影响首次登录能否进行，由
/// <see cref="AdminDatabaseInitializer"/> 记警告。
/// </remarks>
internal sealed class AdminOptionsValidator : IValidateOptions<AdminOptions>
{
    /// <summary>HS256 对签名密钥长度的最低要求（256 位）。</summary>
    private const int MinimumSigningKeyBytes = 32;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (Encoding.UTF8.GetByteCount(options.Auth.SigningKey) < MinimumSigningKeyBytes)
        {
            return ValidateOptionsResult.Fail(
                $"已启用后台管理（{AdminOptions.SectionName}:Enabled = true），"
                + $"但 {AdminOptions.SectionName}:Auth:SigningKey 缺失或不足 {MinimumSigningKeyBytes} 字节。"
                + "请配置一个足够长的随机密钥（例如 32 字节随机数的 Base64 串），"
                + $"或用环境变量 {AdminOptions.SectionName}__Auth__SigningKey 注入；"
                + $"若不需要后台管理，请设 {AdminOptions.SectionName}:Enabled = false。");
        }

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            return ValidateOptionsResult.Fail(
                $"{AdminOptions.SectionName}:DatabasePath 不能为空。");
        }

        return ValidateOptionsResult.Success;
    }
}
