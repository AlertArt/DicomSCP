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
            TotalCount = 3,
            RemoteHost = "10.0.0.5",
            RemotePort = 104,
            ExpireTime = DateTime.Now.AddDays(30)
        };
        var added = await _repository.AddTransactionAsync(record);
        Assert.True(added);

        var fetched = await _repository.GetTransactionAsync("1.2.3.4.99.1");
        Assert.NotNull(fetched);
        Assert.Equal("MODALITY", fetched.CallingAE);
        Assert.Equal(3, fetched.TotalCount);
        Assert.Equal(StorageCommitmentStatus.Pending.ToString(), fetched.Status);
        Assert.Equal("10.0.0.5", fetched.RemoteHost);
        Assert.Equal(104, fetched.RemotePort);
        Assert.Equal(StorageCommitmentNotificationStatus.Pending.ToString(), fetched.NotificationStatus);
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

        var referenced = new List<ReferencedSopInstance>
        {
            new() { SopClassUid = "1.2.840.10008.5.1.4.1.1.2", SopInstanceUid = "1.2.3.4.99.4", Verified = true },
            new() { SopClassUid = "1.2.840.10008.5.1.4.1.1.2", SopInstanceUid = "1.2.3.4.99.3", Verified = false, FailureReason = 0x0112 }
        };
        var updated = await _repository.UpdateTransactionResultAsync(
            "1.2.3.4.99.2", StorageCommitmentStatus.FailuresExist, referenced.Count, referenced);
        Assert.True(updated);

        var fetched = await _repository.GetTransactionAsync("1.2.3.4.99.2");
        Assert.NotNull(fetched);
        Assert.Equal(StorageCommitmentStatus.FailuresExist.ToString(), fetched.Status);
        Assert.Equal(1, fetched.FailedCount);
        Assert.NotNull(fetched.CompleteTime);
        Assert.Contains("1.2.3.4.99.3", fetched.FailedInstances);
        // 完整引用清单被保存，供重推重建事件报告
        Assert.NotNull(fetched.ReferencedInstances);
        Assert.Contains("1.2.3.4.99.4", fetched.ReferencedInstances);
    }

    [Fact]
    public async Task UpdateNotificationResultAsync_RecordsFailureVisibility()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.10",
            CallingAE = "MODALITY",
            Status = StorageCommitmentStatus.Success.ToString(),
            TotalCount = 1
        });

        await _repository.UpdateNotificationResultAsync("1.2.3.4.99.10", false, "connection refused");
        await _repository.UpdateNotificationResultAsync("1.2.3.4.99.10", false, "connection refused");

        var fetched = await _repository.GetTransactionAsync("1.2.3.4.99.10");
        Assert.NotNull(fetched);
        Assert.Equal(StorageCommitmentNotificationStatus.Failed.ToString(), fetched.NotificationStatus);
        Assert.Equal(2, fetched.NotificationAttempts);
        Assert.Equal("connection refused", fetched.LastNotificationError);
    }

    [Fact]
    public async Task GetTransactionsAsync_FiltersByStatusAndExpiry()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.20",
            CallingAE = "MODALITY",
            Status = StorageCommitmentStatus.FailuresExist.ToString(),
            ExpireTime = DateTime.Now.AddDays(30)
        });
        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.21",
            CallingAE = "MODALITY",
            Status = StorageCommitmentStatus.Success.ToString(),
            ExpireTime = DateTime.Now.AddDays(30)
        });
        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.22",
            CallingAE = "MODALITY",
            Status = StorageCommitmentStatus.FailuresExist.ToString(),
            ExpireTime = DateTime.Now.AddDays(-1) // 已过期
        });

        // 默认排除过期
        var active = await _repository.GetTransactionsAsync();
        Assert.Equal(2, active.Count);
        Assert.DoesNotContain(active, r => r.TransactionUid == "1.2.3.4.99.22");

        // 包含过期
        var all = await _repository.GetTransactionsAsync(includeExpired: true);
        Assert.Equal(3, all.Count);

        // 按状态过滤（含过期）
        var failures = await _repository.GetTransactionsAsync(
            StorageCommitmentStatus.FailuresExist.ToString(), includeExpired: true);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesExpiredOnly()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.30",
            Status = StorageCommitmentStatus.Success.ToString(),
            ExpireTime = DateTime.Now.AddDays(-2)
        });
        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.31",
            Status = StorageCommitmentStatus.Success.ToString(),
            ExpireTime = DateTime.Now.AddDays(2)
        });
        await _repository.AddTransactionAsync(new StorageCommitmentRecord
        {
            TransactionUid = "1.2.3.4.99.32",
            Status = StorageCommitmentStatus.Success.ToString(),
            ExpireTime = null // 永不过期
        });

        var removed = await _repository.PurgeExpiredAsync(DateTime.Now);
        Assert.Equal(1, removed);

        Assert.Null(await _repository.GetTransactionAsync("1.2.3.4.99.30"));
        Assert.NotNull(await _repository.GetTransactionAsync("1.2.3.4.99.31"));
        Assert.NotNull(await _repository.GetTransactionAsync("1.2.3.4.99.32"));
    }
}
