using Dapper;
using DicomSCP.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DicomSCP.Repository;

/// <summary>
/// 存储承诺（Storage Commitment）事务持久化仓储。
/// 记录失败实例与 N-EVENT-REPORT 投递状态，支持失败可见、重推与 TTL 过期清理。
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
                    FailedInstances, NotificationStatus, NotificationAttempts, LastNotificationError,
                    RemoteHost, RemotePort, ExpireTime, ReferencedInstances,
                    CreateTime, CompleteTime
                ) VALUES (
                    @TransactionUid, @CallingAE, @Status, @TotalCount, @FailedCount,
                    @FailedInstances, @NotificationStatus, @NotificationAttempts, @LastNotificationError,
                    @RemoteHost, @RemotePort, @ExpireTime, @ReferencedInstances,
                    @CreateTime, @CompleteTime
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

    /// <summary>列出存储承诺事务；可按状态过滤、是否包含已过期。</summary>
    public async Task<List<StorageCommitmentRecord>> GetTransactionsAsync(
        string? status = null,
        bool includeExpired = false,
        int limit = 100,
        int offset = 0)
    {
        try
        {
            using var connection = CreateConnection();
            const string sql = @"
                SELECT * FROM StorageCommitments
                WHERE (@Status = '' OR Status = @Status)
                  AND (@IncludeExpired = 1 OR ExpireTime IS NULL OR ExpireTime > @Now)
                ORDER BY CreateTime DESC
                LIMIT @Limit OFFSET @Offset";
            var result = await connection.QueryAsync<StorageCommitmentRecord>(sql, new
            {
                Status = status ?? string.Empty,
                IncludeExpired = includeExpired,
                Now = DateTime.Now,
                Limit = limit,
                Offset = offset
            });
            return result.ToList();
        }
        catch (Exception ex)
        {
            LogError(ex, "查询存储承诺事务列表失败 - Status: {Status}", status ?? string.Empty);
            throw;
        }
    }

    /// <summary>记录事务校验结果（成功/存在失败），并保存完整引用清单用于重推。</summary>
    public async Task<bool> UpdateTransactionResultAsync(
        string transactionUid,
        StorageCommitmentStatus status,
        int totalCount,
        List<ReferencedSopInstance> referenced)
    {
        try
        {
            using var connection = CreateConnection();
            const string sql = @"
                UPDATE StorageCommitments SET
                    Status = @Status,
                    TotalCount = @TotalCount,
                    FailedCount = @FailedCount,
                    FailedInstances = @FailedInstances,
                    ReferencedInstances = @ReferencedInstances,
                    CompleteTime = @CompleteTime
                WHERE TransactionUid = @TransactionUid";

            var failed = referenced.Where(r => !r.Verified).ToList();
            var failedJson = failed.Count > 0
                ? JsonSerializer.Serialize(failed.Select(f => f.SopInstanceUid).ToList())
                : null;

            var affected = await connection.ExecuteAsync(sql, new
            {
                TransactionUid = transactionUid,
                Status = status.ToString(),
                TotalCount = totalCount,
                FailedCount = failed.Count,
                FailedInstances = failedJson,
                ReferencedInstances = JsonSerializer.Serialize(referenced),
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

    /// <summary>记录 N-EVENT-REPORT 投递结果（成功/失败），失败时保留错误信息供排查与重推。</summary>
    public async Task<bool> UpdateNotificationResultAsync(string transactionUid, bool sent, string? error)
    {
        try
        {
            using var connection = CreateConnection();
            const string sql = @"
                UPDATE StorageCommitments SET
                    NotificationStatus = @NotificationStatus,
                    NotificationAttempts = NotificationAttempts + 1,
                    LastNotificationError = @LastNotificationError
                WHERE TransactionUid = @TransactionUid";
            var affected = await connection.ExecuteAsync(sql, new
            {
                TransactionUid = transactionUid,
                NotificationStatus = (sent
                    ? StorageCommitmentNotificationStatus.Sent
                    : StorageCommitmentNotificationStatus.Failed).ToString(),
                LastNotificationError = sent ? null : error
            });
            return affected > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "更新存储承诺通知结果失败 - TransactionUid: {TransactionUid}", transactionUid);
            throw;
        }
    }

    /// <summary>删除已过期记录，返回删除条数。</summary>
    public async Task<int> PurgeExpiredAsync(DateTime now)
    {
        try
        {
            using var connection = CreateConnection();
            return await connection.ExecuteAsync(
                "DELETE FROM StorageCommitments WHERE ExpireTime IS NOT NULL AND ExpireTime <= @Now",
                new { Now = now });
        }
        catch (Exception ex)
        {
            LogError(ex, "清理过期存储承诺记录失败");
            throw;
        }
    }
}
