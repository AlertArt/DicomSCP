using Dapper;
using DicomSCP.Repository;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DicomSCP.Tests;

public class DatabaseInitializerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Initialize_FirstRun_ReturnsTrue_AndCreatesAllTables()
    {
        var isFirstInit = await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        Assert.True(isFirstInit);

        await using var connection = new SqliteConnection(_db.ConnectionString);
        await connection.OpenAsync();

        var expectedTables = new[]
        {
            "Patients", "Studies", "Series", "Instances",
            "Worklist", "Users", "PrintJobs"
        };

        foreach (var table in expectedTables)
        {
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@Table",
                new { Table = table });
            Assert.True(count == 1, $"建表缺失: {table}");
        }
    }

    [Fact]
    public async Task Initialize_SecondRun_ReturnsFalse_AndKeepsSchemaIdempotent()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var secondRun = await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        Assert.False(secondRun);
    }

    [Fact]
    public async Task Initialize_CreatesDefaultAdminUser()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await using var connection = new SqliteConnection(_db.ConnectionString);
        await connection.OpenAsync();

        var userCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = 'admin'");

        Assert.Equal(1, userCount);
    }
}
