using System.Security.Cryptography;

namespace DicomSCP.Repository;

/// <summary>
/// 口令哈希与校验。
/// 存储格式（自包含、可演进）：PBKDF2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
/// 历史格式：无盐 SHA-256（Base64）——校验通过时标记 needsUpgrade，
/// 由调用方在登录成功后透明升级为 PBKDF2，无需用户重置口令。
/// </summary>
public static class PasswordHasher
{
    public const string FormatPrefix = "PBKDF2";

    /// <summary>默认迭代次数（PBKDF2-HMAC-SHA256）。</summary>
    public const int DefaultIterations = 210_000;

    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>生成带随机盐的 PBKDF2 哈希（每次调用盐都不同）。</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("口令不能为空", nameof(password));
        }
        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "迭代次数必须大于 0");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{FormatPrefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 校验口令与存储值是否匹配。历史无盐格式校验通过时 needsUpgrade = true。
    /// </summary>
    public static bool Verify(string? password, string? stored, out bool needsUpgrade)
    {
        needsUpgrade = false;

        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        if (stored.StartsWith(FormatPrefix + "$", StringComparison.Ordinal))
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 ||
                !int.TryParse(parts[1], out var iterations) ||
                iterations < 1)
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // 历史格式：无盐 SHA-256（Base64），定长 44 字符
        needsUpgrade = true;
        var legacy = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password)));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(legacy),
            System.Text.Encoding.UTF8.GetBytes(stored));
    }
}
