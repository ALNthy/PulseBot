using Microsoft.Extensions.Options;
using AnBot.Admin.Infrastructure;
using AnBot.Admin.Shared;

namespace AnBot.Admin.Tests;

/// <summary>
/// 后台配置校验测试：启用后台时的必填项在启动期就被拦下，而不是等第一次登录才暴露。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminOptionsValidatorTests
{
    /// <summary>恰好 32 字节的密钥（HS256 的最低要求）。</summary>
    private const string ValidKey = "0123456789abcdef0123456789abcdef";

    private static readonly AdminOptionsValidator Validator = new();

    /// <summary>启用后台且密钥足够长时校验通过。</summary>
    [Fact]
    public void 密钥合法时校验通过()
    {
        ValidateOptionsResult result = Validator.Validate(null, NewOptions(ValidKey));

        Assert.True(result.Succeeded);
    }

    /// <summary>启用后台但密钥缺失或过短时校验失败，提示中指明是哪一个配置项。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcde")]   // 31 字节，差一个
    public void 密钥缺失或过短时校验失败(string signingKey)
    {
        ValidateOptionsResult result = Validator.Validate(null, NewOptions(signingKey));

        Assert.True(result.Failed);
        Assert.Contains("SigningKey", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>后台关闭时不要求密钥：此时根本不挂载后台，没有密钥不算配置错误。</summary>
    [Fact]
    public void 后台关闭时不校验密钥()
    {
        AdminOptions options = NewOptions(signingKey: string.Empty);
        options.Enabled = false;

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    /// <summary>数据库路径为空时校验失败。</summary>
    [Fact]
    public void 数据库路径为空时校验失败()
    {
        AdminOptions options = NewOptions(ValidKey);
        options.DatabasePath = "  ";

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("DatabasePath", result.FailureMessage, StringComparison.Ordinal);
    }

    // ---------- 测试基础设施 ----------

    private static AdminOptions NewOptions(string signingKey) => new()
    {
        Enabled = true,
        DatabasePath = "test.db",
        Auth = new AdminAuthOptions { SigningKey = signingKey },
    };
}
