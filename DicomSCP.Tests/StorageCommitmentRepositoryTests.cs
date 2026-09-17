using DicomSCP.Models;
using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// 存储承诺（Storage Commitment）事务仓储测试。
/// </summary>
public class StorageCommitmentRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly StorageCommitmentRepository _repository;

    public StorageCommitmentRepositoryTests()
    {
        _repository = new StorageCommitmentRepository(_db.Config);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task AddAndGetTransaction_RoundTrips()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var record = new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.1",
            CallingAE = "MODALITY",
            Status = StorageCommitmentStatus.Pending.ToString(),
            TotalCount = 3
        };
        var added = await _repository.AddTransactionAsync(record);
        Assert.True(added);

        var fetched = await _repository.GetTransactionAsync("1.2.3.4.99.1");
        Assert.NotNull(fetched);
        Assert.Equal("MODALITY", fetched.CallingAE);
        Assert.Equal(3, fetched.TotalCount);
        Assert.Equal(StorageCommitmentStatus.Pending.ToString(), fetched.Status);
    }

    [Fact]
    public async Task GetTransaction_UnknownUid_ReturnsNull()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var fetched = await _repository.GetTransactionAsync("9.9.9.9.9");

        Assert.Null(fetched);
    }

    [Fact]
    public async Task UpdateTransactionResultAsync_RecordsFailuresAndCompletion()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var record = new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.2",
            CallingAE = "MODALITY",
            Status = StorageCommitmentStatus.Pending.ToString(),
            TotalCount = 2
        };
        await _repository.AddTransactionAsync(record);

        var failed = new List<ReferencedSopInstance>
        {
            new() { SopClassUid = "1.2.840.10008.5.1.4.1.1.2", SopInstanceUid = "1.2.3.4.99.3", Verified = false, FailureReason = 0x0112 }
        };
        var updated = await _repository.UpdateTransactionResultAsync(
            "1.2.3.4.99.2", StorageCommitmentStatus.FailuresExist, failed);
        Assert.True(updated);

        var fetched = await _repository.GetTransactionAsync("1.2.3.4.99.2");
        Assert.NotNull(fetched);
        Assert.Equal(StorageCommitmentStatus.FailuresExist.ToString(), fetched.Status);
        Assert.Equal(1, fetched.FailedCount);
        Assert.NotNull(fetched.CompleteTime);
        Assert.Contains("1.2.3.4.99.3", fetched.FailedInstances);
    }
}