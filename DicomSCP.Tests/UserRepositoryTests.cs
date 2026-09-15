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
}
