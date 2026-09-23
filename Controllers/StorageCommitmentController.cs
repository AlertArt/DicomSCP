using System.Text.Json;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;
using DicomSCP.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DicomSCP.Controllers;

/// <summary>
/// 存储承诺（Storage Commitment）事务查询与运维：
/// 列出/查看事务、对投递失败的 N-EVENT-REPORT 重推（reseat）、清理过期记录。
/// 路由位于 /api/StorageCommitment，受全局 /api/ 认证中间件保护。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StorageCommitmentController : ControllerBase
{
    private readonly StorageCommitmentRepository _repository;
    private readonly DicomSettings _settings;

    public StorageCommitmentController(StorageCommitmentRepository repository, IOptions<DicomSettings> settings)
    {
        _repository = repository;
        _settings = settings.Value;
    }

    /// <summary>列出存储承诺事务（可按状态过滤，默认排除已过期）。</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status = null,
        [FromQuery] bool includeExpired = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 500) pageSize = 50;

            var items = await _repository.GetTransactionsAsync(
                status, includeExpired, pageSize, (page - 1) * pageSize);
            return Ok(items);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitment", ex, "查询存储承诺列表失败");
            return StatusCode(500, "查询存储承诺列表失败");
        }
    }

    /// <summary>查看单个存储承诺事务详情。</summary>
    [HttpGet("{transactionUid}")]
    public async Task<IActionResult> Get(string transactionUid)
    {
        try
        {
            var record = await _repository.GetTransactionAsync(transactionUid);
            return record == null ? NotFound("Transaction not found") : Ok(record);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitment", ex, "查询存储承诺事务失败 - Transaction: {TransactionUid}", transactionUid);
            return StatusCode(500, "查询存储承诺事务失败");
        }
    }

    /// <summary>对失败（或需重发）的事务重新推送 N-EVENT-REPORT。</summary>
    [HttpPost("{transactionUid}/repush")]
    public async Task<IActionResult> Repush(string transactionUid)
    {
        try
        {
            var record = await _repository.GetTransactionAsync(transactionUid);
            if (record == null)
            {
                return NotFound("Transaction not found");
            }

            if (string.IsNullOrEmpty(record.RemoteHost) || record.RemotePort is null or <= 0)
            {
                return BadRequest("Transaction has no stored destination; cannot repush");
            }

            if (string.IsNullOrEmpty(record.ReferencedInstances))
            {
                return BadRequest("Transaction has no referenced instances; cannot repush");
            }

            var referenced = JsonSerializer.Deserialize<List<ReferencedSopInstance>>(record.ReferencedInstances) ?? new();
            if (referenced.Count == 0)
            {
                return BadRequest("Transaction has no referenced instances; cannot repush");
            }

            var eventType = StorageCommitmentNotifier.ResolveEventType(referenced);
            var dataset = StorageCommitmentNotifier.BuildEventReportDataset(transactionUid, referenced);
            var (sent, error) = await StorageCommitmentNotifier.SendAsync(
                _settings, record.RemoteHost, record.RemotePort.Value, record.CallingAE, eventType, dataset);

            await _repository.UpdateNotificationResultAsync(transactionUid, sent, error);

            DicomLogger.Information("StorageCommitment", "重推存储承诺事件 - Transaction: {TransactionUid}, 成功: {Sent}, 目标: {Host}:{Port}",
                transactionUid, sent, record.RemoteHost, record.RemotePort);

            return Ok(new
            {
                transactionUid,
                sent,
                error,
                notificationStatus = (sent
                    ? StorageCommitmentNotificationStatus.Sent
                    : StorageCommitmentNotificationStatus.Failed).ToString()
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitment", ex, "重推存储承诺事件失败 - Transaction: {TransactionUid}", transactionUid);
            return StatusCode(500, "重推存储承诺事件失败");
        }
    }

    /// <summary>清理已过期（TTL）的存储承诺记录。</summary>
    [HttpPost("purge-expired")]
    public async Task<IActionResult> PurgeExpired()
    {
        try
        {
            var removed = await _repository.PurgeExpiredAsync(DateTime.Now);
            DicomLogger.Information("StorageCommitment", "清理过期存储承诺记录 - 删除: {Removed}", removed);
            return Ok(new { removed });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitment", ex, "清理过期存储承诺记录失败");
            return StatusCode(500, "清理过期存储承诺记录失败");
        }
    }
}
