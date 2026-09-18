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

    [Fact]
    public async Task Initialize_EnablesWriteAheadLogging()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await using var connection = new SqliteConnection(_db.ConnectionString);
        await connection.OpenAsync();

        var journalMode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;");
        Assert.Equal("wal", journalMode, ignoreCase: true);
    }

    [Fact]
    public async Task Initialize_CreatesQueryIndexes()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await using var connection = new SqliteConnection(_db.ConnectionString);
        await connection.OpenAsync();

        var expectedIndexes = new[]
        {
            "IX_Studies_PatientId",
            "IX_Series_StudyInstanceUid",
            "IX_Instances_SeriesInstanceUid",
            "IX_Worklist_ScheduledAET",
            "IX_StorageCommitments_Status",
            "IX_UpsWorkItems_ScheduledAeTitle"
        };

        foreach (var index in expectedIndexes)
        {
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@Name",
                new { Name = index });
            Assert.True(count == 1, $"索引缺失: {index}");
        }
    }
}
