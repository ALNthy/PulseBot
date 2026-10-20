namespace PulseBot.Adapter.Email.Tests;

/// <summary>
/// 发件人白名单：地址精确匹配、域名匹配与默认拒绝。
/// </summary>
[Trait("Category", "Unit")]
public sealed class EmailSenderPolicyTests
{
    [Fact]
    public void IsAllowed_MatchesSenderAddressCaseInsensitively()
    {
        var policy = new EmailSenderPolicy(["Alice@Example.com"], []);

        Assert.True(policy.IsAllowed("alice@example.com"));
        Assert.True(policy.IsAllowed("ALICE@EXAMPLE.COM"));
    }

    [Fact]
    public void IsAllowed_MatchesSenderDomainCaseInsensitively()
    {
        var policy = new EmailSenderPolicy([], ["Trusted.ORG"]);

        Assert.True(policy.IsAllowed("bob@trusted.org"));
        Assert.False(policy.IsAllowed("bob@other.org"));
    }

    [Fact]
    public void IsAllowed_AcceptsDomainWrittenWithAtSign()
    {
        var policy = new EmailSenderPolicy([], ["@trusted.org"]);

        Assert.True(policy.IsAllowed("bob@trusted.org"));
    }

    [Fact]
    public void IsAllowed_DeniesEverythingWhenNoRulesConfigured()
    {
        var policy = new EmailSenderPolicy([], []);

        Assert.False(policy.HasRules);
        Assert.False(policy.IsAllowed("alice@example.com"));
    }

    [Fact]
    public void IsAllowed_AddressWhitelistDoesNotGrantWholeDomain()
    {
        var policy = new EmailSenderPolicy(["alice@example.com"], []);

        Assert.True(policy.IsAllowed("alice@example.com"));
        Assert.False(policy.IsAllowed("bob@example.com"));
    }

    [Fact]
    public void HasRules_IgnoresBlankEntries()
    {
        var policy = new EmailSenderPolicy(["  "], [""]);

        Assert.False(policy.HasRules);
    }
}
