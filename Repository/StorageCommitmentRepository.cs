using Dapper;
using DicomSCP.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DicomSCP.Repository;

/// <summary>
/// 存储承诺（Storage Commitment）事务持久化仓储。
/// </summary>
public sealed class StorageCommitmentRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    public async Task<bool> AddTransactionAsync(StorageCommitmentRecord record)
    {
        try
        {
            using var connection = CreateConnection();
            const string sql = @"
                INSERT INTO StorageCommitments (
                    TransactionUid, CallingAE, Status, TotalCount, FailedCount,
                    FailedInstances, CreateTime, CompleteTime
                ) VALUES (
                    @TransactionUid, @CallingAE, @Status, @TotalCount, @FailedCount,
                    @FailedInstances, @CreateTime, @CompleteTime
                )";
            return await connection.ExecuteAsync(sql, record) > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "添加存储承诺事务失败 - TransactionUid: {TransactionUid}", record.TransactionUid);
            throw;
        }
    }

    public async Task<StorageCommitmentRecord?> GetTransactionAsync(string transactionUid)
    {
        try
        {
            using var connection = CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<StorageCommitmentRecord>(
                "SELECT * FROM StorageCommitments WHERE TransactionUid = @TransactionUid",
                new { TransactionUid = transactionUid });
        }
        catch (Exception ex)
        {
            LogError(ex, "查询存储承诺事务失败 - TransactionUid: {TransactionUid}", transactionUid);
            throw;
        }
    }

    public async Task<bool> UpdateTransactionResultAsync(
        string transactionUid,
        StorageCommitmentStatus status,
        List<ReferencedSopInstance> failedInstances)
    {
        try
        {
            using var connection = CreateConnection();
            const string sql = @"
                UPDATE StorageCommitments SET
                    Status = @Status,
                    FailedCount = @FailedCount,
                    FailedInstances = @FailedInstances,
                    CompleteTime = @CompleteTime
                WHERE TransactionUid = @TransactionUid";
            var failedJson = failedInstances.Count > 0
                ? JsonSerializer.Serialize(failedInstances.Select(f => f.SopInstanceUid).ToList())
                : null;
            var affected = await connection.ExecuteAsync(sql, new
            {
                TransactionUid = transactionUid,
                Status = status.ToString(),
                FailedCount = failedInstances.Count,
                FailedInstances = failedJson,
                CompleteTime = DateTime.Now
            });
            return affected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新存储承诺事务结果失败 - TransactionUid: {TransactionUid}", transactionUid);
            throw;
        }
    }
}