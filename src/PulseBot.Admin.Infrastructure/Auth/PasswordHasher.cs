using System.Globalization;
using System.Security.Cryptography;

namespace PulseBot.Admin.Infrastructure.Auth;

/// <summary>密码哈希与校验。</summary>
public interface IPasswordHasher
{
    /// <summary>计算密码的派生哈希。</summary>
    /// <param name="password">明文密码。</param>
    string Hash(string password);

    /// <summary>校验密码是否与已存储的哈希匹配。</summary>
    /// <param name="password">待校验的明文密码。</param>
    /// <param name="encodedHash">
    /// 已存储的哈希编码串；传 <c>null</c>（账号不存在）时同样执行一次等价耗时的计算，
    /// 详见 <see cref="Pbkdf2PasswordHasher"/> 的说明。此情形下返回 <c>false</c>。
    /// </param>
    bool Verify(string password, string? encodedHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 密码哈希。
/// </summary>
/// <remarks>
/// <para>
/// 编码格式：<c>pbkdf2-sha256$&lt;迭代次数&gt;$&lt;盐 Base64&gt;$&lt;哈希 Base64&gt;</c>。
/// 迭代次数与盐随哈希一起保存，因此将来调高强度时旧哈希依然可以校验
/// （并可在用户下次登录成功时按新参数重算）。
/// </para>
/// <para>
/// 迭代次数取 210,000：OWASP 对 PBKDF2-HMAC-SHA256 给出的可接受下限，
/// 单次校验在百毫秒量级——后台登录是低频操作，这个代价可接受，而对离线爆破足够昂贵。
/// </para>
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>算法标识，写在编码串首位，便于将来并存其它算法。</summary>
    private const string Algorithm = "pbkdf2-sha256";

    /// <summary>迭代次数。</summary>
    private const int Iterations = 210_000;

    /// <summary>盐长度（字节）。</summary>
    private const int SaltBytes = 16;

    /// <summary>派生哈希长度（字节）。</summary>
    private const int HashBytes = 32;

    /// <summary>
    /// 账号不存在时使用的占位哈希。
    /// </summary>
    /// <remarks>
    /// <see cref="Verify"/> 收到 <c>null</c> 时拿它走完整流程：让「账号不存在」与「密码错误」
    /// 两条路径付出同样的 PBKDF2 代价，堵住按登录接口响应时间枚举有效用户名的时间侧信道。
    /// 用 <see cref="Lazy{T}"/> 而非静态字段直接初始化，是为了不把这次计算压在类型初始化上——
    /// 它只在第一次遇到不存在的账号时才需要。内容是一段随机字节，不对应任何密码，
    /// 因此校验必然失败。
    /// </remarks>
    private static readonly Lazy<string> DummyHash = new(static () => Encode(
        RandomNumberGenerator.GetBytes(SaltBytes),
        Iterations,
        RandomNumberGenerator.GetBytes(HashBytes)));

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return Encode(salt, Iterations, hash);
    }

    /// <inheritdoc />
    public bool Verify(string password, string? encodedHash)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        string target = encodedHash ?? DummyHash.Value;

        if (!TryDecode(target, out int iterations, out byte[] salt, out byte[] expected))
        {
            // 存储的哈希格式损坏：视为校验失败，但不额外付出 PBKDF2 代价——
            // 这是数据损坏而非攻击路径，不构成可枚举的信号。
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // 恒定时间比较：逐字节短路会泄露「前若干字节猜对了」这一信息。
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>把盐、迭代次数与哈希编码为一个字符串。</summary>
    private static string Encode(byte[] salt, int iterations, byte[] hash)
        => $"{Algorithm}${iterations.ToString(CultureInfo.InvariantCulture)}" +
           $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

    /// <summary>解析编码串；格式非法时返回 <c>false</c>。</summary>
    private static bool TryDecode(
        string encoded,
        out int iterations,
        out byte[] salt,
        out byte[] expected)
    {
        iterations = 0;
        salt = [];
        expected = [];

        string[] parts = encoded.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations <= 0)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && expected.Length > 0;
    }
}
