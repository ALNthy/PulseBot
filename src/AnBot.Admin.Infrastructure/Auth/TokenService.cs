using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Infrastructure.Auth;

/// <summary>
/// 令牌服务：签发访问令牌（JWT）、生成与哈希刷新令牌。
/// </summary>
/// <remarks>
/// <para>
/// 两种令牌分工不同：访问令牌是自包含的 JWT，服务端不保存，靠签名与有效期校验；
/// 刷新令牌是不透明随机串，服务端只存它的哈希，因此可以真正撤销。
/// </para>
/// <para>
/// 本类型无状态且线程安全（<see cref="JsonWebTokenHandler"/> 可安全重用），注册为单例。
/// </para>
/// </remarks>
public sealed class TokenService
{
    /// <summary>用户名声明名。与 JwtBearer 的 <c>NameClaimType</c> 配置一致。</summary>
    public const string NameClaim = "name";

    /// <summary>角色声明名。与 JwtBearer 的 <c>RoleClaimType</c> 配置一致。</summary>
    public const string RoleClaim = "role";

    /// <summary>账号主键声明名。</summary>
    public const string UserIdClaim = "uid";

    /// <summary>刷新令牌的随机字节数（256 位）。</summary>
    private const int RefreshTokenBytes = 32;

    private readonly AdminAuthOptions _auth;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials;
    private readonly SecurityKey _signingKey;

    /// <summary>构造函数注入。</summary>
    /// <param name="options">后台配置。</param>
    /// <param name="time">时间源；测试可替换为可控实现。</param>
    /// <exception cref="ArgumentOutOfRangeException">签名密钥为空时抛出（应由启动期校验提前拦截）。</exception>
    public TokenService(IOptions<AdminOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _auth = options.Value.Auth;
        _time = time;

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_auth.SigningKey));
        _credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>令牌校验参数。签发与校验必须共用同一份，否则会出现「签得出、验不过」。</summary>
    /// <remarks>
    /// 后台的 JwtBearer 中间件用这些参数校验入站令牌。
    /// </remarks>
    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _auth.Issuer,
        ValidateAudience = true,
        ValidAudience = _auth.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _signingKey,
        ValidateLifetime = true,

        // 默认 5 分钟时钟偏移对短期访问令牌太宽松——等于把 30 分钟的有效期延长到 35 分钟。
        ClockSkew = TimeSpan.FromSeconds(30),

        NameClaimType = NameClaim,
        RoleClaimType = RoleClaim,
    };

    /// <summary>给账号签发一个访问令牌。</summary>
    /// <param name="user">已通过认证的账号。</param>
    /// <returns>JWT 与它的到期时刻。</returns>
    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AdminUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset expiresAt = now + _auth.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _auth.Issuer,
            Audience = _auth.Audience,
            IssuedAt = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,

            // 只放展示与授权必需的声明：令牌是明文的，不放任何敏感信息。
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [NameClaim] = user.UserName,
                [RoleClaim] = user.Role,
                [UserIdClaim] = user.Id.ToString(CultureInfo.InvariantCulture),
            },
        };

        return (_handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>生成一个刷新令牌。</summary>
    /// <returns>
    /// 明文（发给客户端，仅此一次）与哈希（落库）。
    /// </returns>
    public static (string Plain, string Hash) CreateRefreshToken()
    {
        // 256 位随机值：不存在被猜测的可能，因此库里存 SHA-256 即可，
        // 不需要密码那样的慢哈希，查找也能直接走唯一索引。
        string plain = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenBytes));
        return (plain, HashRefreshToken(plain));
    }

    /// <summary>计算刷新令牌的存储哈希。</summary>
    /// <param name="plain">令牌明文。</param>
    public static string HashRefreshToken(string plain)
    {
        ArgumentException.ThrowIfNullOrEmpty(plain);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));
    }
}
