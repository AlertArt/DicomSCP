using System.Text;
using Dapper;
using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

public class UserRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly UserRepository _repository;

    public UserRepositoryTests()
    {
        _repository = new UserRepository(_db.Config);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task ValidateUser_DefaultAdminCredentials_Succeeds()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var isValid = await _repository.ValidateUserAsync("admin", "admin");

        Assert.True(isValid);
    }

    [Fact]
    public async Task ValidateUser_WrongPassword_Fails()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var isValid = await _repository.ValidateUserAsync("admin", "wrong-password");

        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateUser_UnknownUser_Fails()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var isValid = await _repository.ValidateUserAsync("nobody", "admin");

        Assert.False(isValid);
    }

    [Fact]
    public async Task ChangePassword_Roundtrip_UpdatesCredential()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var changed = await _repository.ChangePasswordAsync("admin", "new-password-123");
        Assert.True(changed);

        Assert.False(await _repository.ValidateUserAsync("admin", "admin"));
        Assert.True(await _repository.ValidateUserAsync("admin", "new-password-123"));
    }

    [Fact]
    public async Task FreshInit_SeedsPbkdf2_WithPerInstallRandomSalt()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var hash = await GetStoredPasswordAsync(_db.ConnectionString, "admin");

        // 新装库必须为 PBKDF2 自包含格式，不再是无盐 SHA-256
        Assert.StartsWith("PBKDF2$", hash);
    }

    [Fact]
    public async Task TwoFreshInstalls_ProduceDifferentSalts()
    {
        using var dbA = new TestDb();
        using var dbB = new TestDb();
        await DatabaseInitializer.InitializeAsync(dbA.ConnectionString);
        await DatabaseInitializer.InitializeAsync(dbB.ConnectionString);

        var hashA = await GetStoredPasswordAsync(dbA.ConnectionString, "admin");
        var hashB = await GetStoredPasswordAsync(dbB.ConnectionString, "admin");

        Assert.StartsWith("PBKDF2$", hashA);
        Assert.StartsWith("PBKDF2$", hashB);
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public async Task LegacySha256Row_VerifiesOnLogin_AndTransparentlyUpgrades()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        // 模拟旧库：管理员行是无盐 SHA-256 存储格式
        var legacyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("admin")));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString))
        {
            await connection.ExecuteAsync(
                "UPDATE Users SET Password = @Legacy WHERE Username = 'admin'",
                new { Legacy = legacyHash });
        }

        // 旧格式口令仍可登录
        Assert.True(await _repository.ValidateUserAsync("admin", "admin"));

        // 登录成功后立即升级为 PBKDF2
        var upgraded = await GetStoredPasswordAsync(_db.ConnectionString, "admin");
        Assert.StartsWith("PBKDF2$", upgraded);
        Assert.NotEqual(legacyHash, upgraded);

        // 升级后口令依然有效
        Assert.True(await _repository.ValidateUserAsync("admin", "admin"));
    }

    [Fact]
    public async Task LegacySha256Row_WrongPassword_DoesNotUpgrade()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var legacyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("admin")));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString))
        {
            await connection.ExecuteAsync(
                "UPDATE Users SET Password = @Legacy WHERE Username = 'admin'",
                new { Legacy = legacyHash });
        }

        Assert.False(await _repository.ValidateUserAsync("admin", "wrong"));

        // 校验失败不得改写存储（保留原格式供下次正确口令升级）
        var stored = await GetStoredPasswordAsync(_db.ConnectionString, "admin");
        Assert.Equal(legacyHash, stored);
    }

    [Fact]
    public async Task FreshInit_SeedsDefaultAdmin_FlaggedForPasswordChange()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        Assert.True(await _repository.IsPasswordChangeRequiredAsync("admin"));
    }

    [Fact]
    public async Task ChangePassword_ClearsMustChangeFlag()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        Assert.True(await _repository.IsPasswordChangeRequiredAsync("admin"));

        await _repository.ChangePasswordAsync("admin", "new-password-123");

        Assert.False(await _repository.IsPasswordChangeRequiredAsync("admin"));
    }

    [Fact]
    public async Task SchemaMigration_AddsFlag_AndFlagsLegacyDefaultPassword()
    {
        // 模拟旧库：没有 MustChangePassword 列，且管理员仍是默认口令
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(@"
                CREATE TABLE Users (
                    Username TEXT PRIMARY KEY,
                    Password TEXT NOT NULL
                )");
        }

        var legacyHash = PasswordHasher.Hash("admin");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString))
        {
            await connection.ExecuteAsync(
                "INSERT INTO Users (Username, Password) VALUES ('admin', @Password)",
                new { Password = legacyHash });
        }

        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        Assert.True(await _repository.IsPasswordChangeRequiredAsync("admin"));
    }

    private static async Task<string?> GetStoredPasswordAsync(string connectionString, string username)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        return await connection.ExecuteScalarAsync<string>(
            "SELECT Password FROM Users WHERE Username = @Username",
            new { Username = username });
    }
}
