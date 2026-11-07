using System.Globalization;
using AnBot.Admin.Infrastructure.Auth;

namespace AnBot.Admin.Tests;

/// <summary>
/// 密码哈希测试：往返校验、盐的随机性、损坏输入的容错，以及账号不存在时的占位校验路径。
/// </summary>
/// <remarks>
/// PBKDF2 迭代 21 万次，单次计算在百毫秒量级，因此每个用例只做必要的几次计算。
/// </remarks>
[Trait("Category", "Unit")]
public sealed class Pbkdf2PasswordHasherTests
{
    /// <summary>哈希后能用原密码校验通过，换一个密码则失败。</summary>
    [Fact]
    public void 哈希与校验往返一致()
    {
        var hasher = new Pbkdf2PasswordHasher();

        string encoded = hasher.Hash("correct horse battery staple");

        Assert.True(hasher.Verify("correct horse battery staple", encoded));
        Assert.False(hasher.Verify("wrong password", encoded));
    }

    /// <summary>相同密码两次哈希得到不同编码串（盐随机），但两者都能校验通过。</summary>
    [Fact]
    public void 相同密码的两次哈希不同但都可校验()
    {
        var hasher = new Pbkdf2PasswordHasher();

        string first = hasher.Hash("same-password");
        string second = hasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify("same-password", first));
        Assert.True(hasher.Verify("same-password", second));
    }

    /// <summary>编码串由算法名、迭代次数、盐、哈希四段组成，将来调高强度后可兼容旧哈希。</summary>
    [Fact]
    public void 编码串包含算法与迭代次数()
    {
        var hasher = new Pbkdf2PasswordHasher();

        string[] parts = hasher.Hash("x").Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.True(int.Parse(parts[1], CultureInfo.InvariantCulture) >= 210_000);
    }

    /// <summary>为空密码计算哈希属于调用方错误，立即抛出而不是产生一个空串就能命中的哈希。</summary>
    [Fact]
    public void 空密码哈希抛出异常()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.Throws<ArgumentException>(() => hasher.Hash(string.Empty));
    }

    /// <summary>
    /// 存储的哈希为 <c>null</c>（账号不存在）时返回 <c>false</c> 且不抛异常。
    /// </summary>
    /// <remarks>
    /// 认证层依赖这条路径：账号不存在时也要付出一次等价的 PBKDF2 代价，
    /// 让「账号不存在」与「密码错误」的响应耗时一致。
    /// </remarks>
    [Fact]
    public void 哈希为null时校验失败而不抛异常()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.False(hasher.Verify("any-password", null));
    }

    /// <summary>编码串损坏或格式不识别时按校验失败处理，不抛异常。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$0$c2FsdA==$aGFzaA==")]   // 迭代次数为 0
    [InlineData("pbkdf2-sha256$1000$!!!$aGFzaA==")]     // 盐不是合法 Base64
    [InlineData("md5$1000$c2FsdA==$aGFzaA==")]          // 未知算法
    public void 损坏的编码串校验失败(string encodedHash)
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.False(hasher.Verify("any-password", encodedHash));
    }

    /// <summary>空密码直接判失败，不进入哈希计算。</summary>
    [Fact]
    public void 空密码校验失败()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.False(hasher.Verify(string.Empty, hasher.Hash("real-password")));
    }
}
