using System.Collections.Frozen;

namespace PulseBot.Adapter.Email;

/// <summary>
/// 发件人准入策略：只放行配置中列出的地址或域名。
/// </summary>
/// <remarks>
/// <para>
/// 邮件头的 From 可以伪造，因此「默认拒绝 + 显式白名单」是唯一安全的默认值。
/// 真正的身份校验（SPF/DKIM/DMARC）由收信服务商完成，本适配器不重复实现。
/// </para>
/// <para>
/// 规则在构造时规范化并冻结，运行期只做两次集合查找，不产生分配，
/// 与 Core 的 <c>AccessPolicySnapshot</c> 使用同一种模式。
/// </para>
/// </remarks>
public sealed class EmailSenderPolicy
{
    /// <summary>允许的发件地址（已转小写）。</summary>
    private readonly FrozenSet<string> _senders;

    /// <summary>允许的发件域名（已转小写且不含 <c>@</c>）。</summary>
    private readonly FrozenSet<string> _domains;

    /// <summary>由配置中的地址与域名列表构造策略；空白项会被忽略。</summary>
    /// <param name="senders">允许的完整发件地址。</param>
    /// <param name="domains">允许的发件域名。</param>
    public EmailSenderPolicy(IEnumerable<string> senders, IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(domains);

        _senders = senders
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .ToFrozenSet(StringComparer.Ordinal);

        _domains = domains
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => NormalizeDomain(value))
            .Where(static value => value.Length > 0)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>是否配置了任何准入规则；未配置时所有邮件都被拒绝。</summary>
    public bool HasRules => _senders.Count > 0 || _domains.Count > 0;

    /// <summary>判断发件地址是否允许通过。</summary>
    /// <param name="address">发件地址（大小写不敏感）。</param>
    public bool IsAllowed(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        string normalized = address.Trim().ToLowerInvariant();
        if (_senders.Contains(normalized))
        {
            return true;
        }

        // 域名匹配只取最后一个 @ 之后的部分：地址本地部分允许出现 @（带引号的形式），
        // 这里不解析引号语法，按最后一个 @ 切分对常见的合法地址已经足够。
        int at = normalized.LastIndexOf('@');
        return at >= 0 && _domains.Contains(normalized[(at + 1)..]);
    }

    /// <summary>域名规范化：去空白、转小写、去掉误写的 <c>@</c> 前缀。</summary>
    private static string NormalizeDomain(string value)
    {
        string domain = value.Trim().ToLowerInvariant();
        return domain.StartsWith('@') ? domain[1..] : domain;
    }
}
