using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using AnBot.Admin.Infrastructure.Auth;
using AnBot.Admin.Infrastructure.Entities;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Tests;

/// <summary>
/// 令牌服务测试：访问令牌的签发与校验闭环、过期与伪造的拒绝、刷新令牌的随机性与哈希。
/// </summary>
[Trait("Category", "Unit")]
public sealed class TokenServiceTests
{
    private const string SigningKey = "unit-test-signing-key-0123456789abcdef";
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>
    /// 「刚才」——只关心签名与声明的用例用它签发。
    /// </summary>
    /// <remarks>
    /// 校验用的是系统时钟（<c>ValidateLifetime</c> 不受被测服务的 <see cref="MutableTimeProvider"/>
    /// 控制），因此这类用例必须以真实当前时刻签发，否则令牌一签发就已过期，
    /// 用例会因为「过期」而不是因为「密钥不对」「签名被改」而失败——那是假阳性。
    /// </remarks>
    private static readonly DateTimeOffset JustNow = DateTimeOffset.UtcNow;

    /// <summary>签发的访问令牌能通过同一份校验参数验证，三个自定义声明齐全。</summary>
    /// <remarks>
    /// 这条用例同时守住「签发与校验用同一套配置」这个不变量：
    /// 两边一旦漂移（例如声明名不一致），验证会立刻失败而不是等到线上才发现。
    /// </remarks>
    [Fact]
    public async Task 签发的访问令牌可通过自身校验参数验证()
    {
        TokenService service = CreateService(new MutableTimeProvider(JustNow));

        (string token, _) = service.CreateAccessToken(NewUser(id: 7, userName: "alice", role: "admin"));

        TokenValidationResult result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, service.CreateValidationParameters());

        Assert.True(result.IsValid);
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("alice", jwt.GetClaim(TokenService.NameClaim).Value);
        Assert.Equal("admin", jwt.GetClaim(TokenService.RoleClaim).Value);
        Assert.Equal("7", jwt.GetClaim(TokenService.UserIdClaim).Value);
    }

    /// <summary>到期时刻等于签发时刻加上配置的有效期。</summary>
    [Fact]
    public void 到期时刻等于签发时刻加有效期()
    {
        TokenService service = CreateService(new MutableTimeProvider(Start), accessLifetime: TimeSpan.FromMinutes(30));

        (_, DateTimeOffset expiresAt) = service.CreateAccessToken(NewUser());

        Assert.Equal(Start + TimeSpan.FromMinutes(30), expiresAt);
    }

    /// <summary>已过期的访问令牌校验不通过。</summary>
    [Fact]
    public async Task 过期的访问令牌校验失败()
    {
        // 签发时刻在两小时前（有效期 30 分钟），远超 30 秒的时钟偏移宽容。
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        TokenService service = CreateService(clock);

        (string token, _) = service.CreateAccessToken(NewUser());

        TokenValidationResult result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, service.CreateValidationParameters());

        Assert.False(result.IsValid);
    }

    /// <summary>用另一个密钥签发的令牌不被接受——签名校验确实在起作用。</summary>
    [Fact]
    public async Task 其它密钥签发的令牌校验失败()
    {
        TokenService service = CreateService(new MutableTimeProvider(JustNow));
        TokenService impostor = CreateService(
            new MutableTimeProvider(JustNow),
            signingKey: "another-signing-key-0123456789abcdefgh");

        (string token, _) = impostor.CreateAccessToken(NewUser());

        TokenValidationResult result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token, service.CreateValidationParameters());

        Assert.False(result.IsValid);
    }

    /// <summary>签名被改动过的令牌不被接受。</summary>
    [Fact]
    public async Task 被篡改的令牌校验失败()
    {
        TokenService service = CreateService(new MutableTimeProvider(JustNow));
        (string token, _) = service.CreateAccessToken(NewUser());

        // 改签名段的**首个**字符，而不是末位：Base64 每 4 个字符编码 3 字节，
        // HMAC-SHA256 的 256 位签名编码成 43 个字符时，末位字符只有 4 位参与解码，
        // 其余是填充。因而在 'A' 与 'B' 之间来回改末位，当原字符的高 4 位恰为 0
        // （即字符是 A/B/C/D 之一，约 1/16 的概率）时解码出的签名字节完全相同——
        // 令牌其实没被改动，校验照样通过，这个断言就会随机失败。
        // 首字符的 6 位全部有效，任何改动都必然改变签名字节。
        int signatureStart = token.LastIndexOf('.') + 1;
        char first = token[signatureStart];
        string tampered = token[..signatureStart]
            + (first == 'A' ? 'B' : 'A')
            + token[(signatureStart + 1)..];

        TokenValidationResult result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(tampered, service.CreateValidationParameters());

        Assert.False(result.IsValid);
    }

    /// <summary>刷新令牌每次生成都不同；同一明文的哈希稳定，且哈希不等于明文。</summary>
    [Fact]
    public void 刷新令牌每次不同且哈希稳定()
    {
        (string firstPlain, string firstHash) = TokenService.CreateRefreshToken();
        (string secondPlain, string secondHash) = TokenService.CreateRefreshToken();

        Assert.NotEqual(firstPlain, secondPlain);
        Assert.NotEqual(firstHash, secondHash);

        // 同一明文每次哈希结果一致，令牌查找才能走唯一索引。
        Assert.Equal(firstHash, TokenService.HashRefreshToken(firstPlain));
        Assert.NotEqual(firstPlain, firstHash);
    }

    /// <summary>空明文无法参与哈希：调用方必须先做非空判断。</summary>
    [Fact]
    public void 空刷新令牌哈希抛出异常()
    {
        Assert.Throws<ArgumentException>(() => TokenService.HashRefreshToken(string.Empty));
    }

    // ---------- 测试基础设施 ----------

    private static TokenService CreateService(
        MutableTimeProvider clock,
        TimeSpan? accessLifetime = null,
        string signingKey = SigningKey)
    {
        var options = new AdminOptions
        {
            Auth = new AdminAuthOptions
            {
                SigningKey = signingKey,
                Issuer = "anbot-test",
                Audience = "anbot-test",
                AccessTokenLifetime = accessLifetime ?? TimeSpan.FromMinutes(30),
            },
        };

        return new TokenService(Options.Create(options), clock);
    }

    private static AdminUser NewUser(int id = 1, string userName = "tester", string role = "admin")
        => new() { Id = id, UserName = userName, Role = role };
}
