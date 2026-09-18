using Dapper;
using DicomSCP.Services;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

public sealed class UserRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    private readonly int _iterations = configuration.GetValue("Auth:PasswordIterations", PasswordHasher.DefaultIterations);

    /// <summary>
    /// 校验用户口令。兼容历史无盐 SHA-256 存储格式：
    /// 历史格式校验通过后立即透明升级为 PBKDF2（无需用户重置口令）。
    /// </summary>
    public async Task<bool> ValidateUserAsync(string username, string password)
    {
        await using var connection = CreateConnection();

        var stored = await connection.QueryFirstOrDefaultAsync<string?>(
            "SELECT Password FROM Users WHERE Username = @Username",
            new { Username = username });

        if (stored == null)
        {
            return false;
        }

        if (!PasswordHasher.Verify(password, stored, out var needsUpgrade))
        {
            return false;
        }

        if (needsUpgrade)
        {
            await UpgradeLegacyHashAsync(connection, username, stored, password);
        }

        return true;
    }

    public async Task<bool> ChangePasswordAsync(string username, string newPassword)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var sql = @"
            UPDATE Users
            SET Password = @Password,
                MustChangePassword = 0
            WHERE Username = @Username";

        var result = await connection.ExecuteAsync(sql, new
        {
            Username = username,
            Password = PasswordHasher.Hash(newPassword, _iterations)
        });

        return result > 0;
    }

    /// <summary>判断账号是否被标记为必须修改口令（默认口令或管理员重置后）。</summary>
    public async Task<bool> IsPasswordChangeRequiredAsync(string username)
    {
        await using var connection = CreateConnection();
        var required = await connection.ExecuteScalarAsync<int?>(
            "SELECT MustChangePassword FROM Users WHERE Username = @Username",
            new { Username = username });

        return required == 1;
    }

    /// <summary>
    /// 将历史无盐哈希升级为 PBKDF2。带旧值条件更新，避免与并发改密竞争时覆盖新口令。
    /// </summary>
    private async Task UpgradeLegacyHashAsync(SqliteConnection connection, string username, string storedLegacy, string password)
    {
        try
        {
            await connection.ExecuteAsync(
                "UPDATE Users SET Password = @Password WHERE Username = @Username AND Password = @Stored",
                new
                {
                    Username = username,
                    Stored = storedLegacy,
                    Password = PasswordHasher.Hash(password, _iterations)
                });
            LogInformation("已将用户 {Username} 的历史 SHA-256 口令哈希升级为 PBKDF2", username);
        }
        catch (Exception ex)
        {
            // 升级失败不影响本次登录（下次成功登录会再次尝试）
            LogError(ex, "升级口令哈希格式失败 - 用户: {Username}", username);
        }
    }
}
