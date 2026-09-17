using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;

namespace DicomSCP.Services;

/// <summary>
/// 存储服务承诺服务（Storage Commitment Push Model SCP）：
/// 接收 N-ACTION 存储承诺请求，校验后端实例归档情况后，
/// 在新关联上通过 N-EVENT-REPORT 将承诺结果推送给请求方。
/// 参照 PS3.4 Annex J 实现。
/// </summary>
public class StorageCommitmentSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private static DicomSettings? _globalSettings;
    private static DicomRepository? _globalRepository;
    private static StorageCommitmentRepository? _globalCommitmentRepository;

    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;
    private readonly StorageCommitmentRepository _commitmentRepository;

    // Action Type ID：1 = 存储 SOP 实例（Store SOP Instances）
    private const ushort ActionTypeStoreSopInstances = 1;

    // N-EVENT-REPORT 事件类型：1 = 成功，2 = 存在失败
    private const ushort EventReportSuccess = 1;
    private const ushort EventReportFailuresExist = 2;

    // FailureReason：0112 = No Such Object Instance（实例未被归档）
    private const ushort FailureReasonNoSuchObjectInstance = 0x0112;
    private const ushort FailureReasonProcessingFailure = 0x0110;

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    public static void Configure(DicomSettings settings, DicomRepository repository, StorageCommitmentRepository commitmentRepository)
    {
        _globalSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _globalRepository = repository ?? throw new ArgumentNullException(nameof(repository));
        _globalCommitmentRepository = commitmentRepository ?? throw new ArgumentNullException(nameof(commitmentRepository));
    }

    public StorageCommitmentSCP(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = _globalSettings ?? throw new InvalidOperationException("StorageCommitmentSCP is not configured");
        _repository = _globalRepository ?? throw new InvalidOperationException("StorageCommitmentSCP is not configured");
        _commitmentRepository = _globalCommitmentRepository ?? throw new InvalidOperationException("StorageCommitmentSCP is not configured");
    }

    // ── 关联处理 ────────────────────────────────────────────────────────────

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("StorageCommitmentSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                association.CalledAE, association.CallingAE);

            var rejectReason = AssociationGuard.Validate(
                association,
                _settings.StorageCommitmentSCP.AeTitle,
                _settings.StorageCommitmentSCP.ValidateCallingAE,
                _settings.StorageCommitmentSCP.AllowedCallingAEs ?? Array.Empty<string>());

            if (rejectReason.HasValue)
            {
                DicomLogger.Warning("StorageCommitmentSCP", "拒绝关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}, 原因: {Reason}",
                    association.CalledAE, association.CallingAE, rejectReason);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    rejectReason.Value);
            }

            var supportedSOPClasses = new DicomUID[]
            {
                DicomUID.StorageCommitmentPushModel,
                DicomUID.Verification // C-ECHO
            };

            var hasValidPresentationContext = false;
            foreach (var pc in association.PresentationContexts)
            {
                if (!supportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    DicomLogger.Warning("StorageCommitmentSCP", "不支持的服务类型：{AbstractSyntax}", pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    continue;
                }

                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                hasValidPresentationContext = true;
            }

            if (!hasValidPresentationContext)
            {
                DicomLogger.Warning("StorageCommitmentSCP", "没有有效的表示上下文，拒绝关联请求");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.ApplicationContextNotSupported);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitmentSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.ApplicationContextNotSupported);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("StorageCommitmentSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("StorageCommitmentSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("StorageCommitmentSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("StorageCommitmentSCP", "连接正常关闭");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("StorageCommitmentSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    // ── N-服务处理 ──────────────────────────────────────────────────────────

    /// <summary>
    /// N-ACTION：接收存储承诺请求（ActionTypeID = 1），校验后端归档情况后返回 N-ACTION-RSP，
    /// 随后在新关联上推送 N-EVENT-REPORT。
    /// </summary>
    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try
        {
            if (request.SOPClassUID != DicomUID.StorageCommitmentPushModel)
            {
                DicomLogger.Warning("StorageCommitmentSCP", "不支持的 SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
                return new DicomNActionResponse(request, DicomStatus.SOPClassNotSupported);
            }

            if (request.ActionTypeID != ActionTypeStoreSopInstances)
            {
                DicomLogger.Warning("StorageCommitmentSCP", "不支持的 Action Type: {ActionType}", request.ActionTypeID);
                return new DicomNActionResponse(request, DicomStatus.InvalidArgumentValue);
            }

            var transactionUid = request.Dataset?.GetSingleValueOrDefault<string>(DicomTag.TransactionUID, string.Empty) ?? string.Empty;
            var referenced = ExtractReferencedInstances(request.Dataset);

            if (string.IsNullOrEmpty(transactionUid) || referenced.Count == 0)
            {
                DicomLogger.Warning("StorageCommitmentSCP", "存储承诺请求缺少 TransactionUID 或 ReferencedSOPSequence");
                return new DicomNActionResponse(request, DicomStatus.InvalidAttributeValue);
            }

            DicomLogger.Information("StorageCommitmentSCP", "收到存储承诺请求 - Transaction: {TransactionUid}, 引用实例数: {Count}, Calling AE: {CallingAE}",
                transactionUid, referenced.Count, Association.CallingAE);

            // 逐个校验实例是否已成功归档
            foreach (var item in referenced)
            {
                item.Verified = await IsInstanceStoredAsync(item.SopInstanceUid);
                if (!item.Verified)
                {
                    DicomLogger.Warning("StorageCommitmentSCP", "实例未归档 - SOPInstanceUID: {SopInstanceUid}", item.SopInstanceUid);
                }
            }

            var failed = referenced.Where(r => !r.Verified).ToList();

            // 持久化承诺事务记录
            var record = new StorageCommitmentRecord
            {
                TransactionUid = transactionUid,
                CallingAE = Association.CallingAE,
                Status = StorageCommitmentStatus.Pending.ToString(),
                TotalCount = referenced.Count
            };
            try
            {
                await _commitmentRepository.AddTransactionAsync(record);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("StorageCommitmentSCP", ex, "保存存储承诺事务失败 - Transaction: {TransactionUid}", transactionUid);
            }

            // 返回 N-ACTION-RSP
            var response = CreateActionResponse(request);

            // 在新关联上异步推送 N-EVENT-REPORT（遵循 PS3.4 要求另开关联发送）
            var hostingHost = Association.RemoteHost;
            var hostingPort = Association.RemotePort;
            var callingAe = Association.CallingAE;
            _ = Task.Run(() => PushEventReportAsync(
                transactionUid, referenced, failed, hostingHost, hostingPort, callingAe));

            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitmentSCP", ex, "处理 N-ACTION 请求失败");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    /// <summary>
    /// N-EVENT-REPORT：本服务作为 SCP 也可能收到对端推送的事件报告，按成功响应处理。
    /// </summary>
    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        DicomLogger.Information("StorageCommitmentSCP", "收到 N-EVENT-REPORT 请求 - EventTypeID: {EventType}", request.EventTypeID);
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    // 存储承诺 Push Model 仅使用 N-ACTION，其余 N 服务返回不支持。

    public Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        return Task.FromResult(new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        return Task.FromResult(new DicomNSetResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        return Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.SOPClassNotSupported));
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private List<ReferencedSopInstance> ExtractReferencedInstances(DicomDataset? dataset)
    {
        var result = new List<ReferencedSopInstance>();
        if (dataset == null)
        {
            return result;
        }

        var sequence = dataset.GetSequence(DicomTag.ReferencedSOPSequence);
        if (sequence == null)
        {
            return result;
        }

        foreach (var item in sequence.Items)
        {
            var sopClass = item.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPClassUID, string.Empty);
            var sopInstance = item.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPInstanceUID, string.Empty);
            if (!string.IsNullOrEmpty(sopClass) && !string.IsNullOrEmpty(sopInstance))
            {
                result.Add(new ReferencedSopInstance
                {
                    SopClassUid = sopClass,
                    SopInstanceUid = sopInstance
                });
            }
        }

        return result;
    }

    private async Task<bool> IsInstanceStoredAsync(string sopInstanceUid)
    {
        try
        {
            // 1) 数据库即时查询（已入库实例）
            var dbInstance = await _repository.GetInstanceAsync(sopInstanceUid);
            if (dbInstance != null)
            {
                return true;
            }

            // 2) 磁盘兜底：文件已由 C-STORE 同步写入，数据库批量入库可能存在延迟
            var matches = Directory.Exists(_settings.StoragePath)
                ? Directory.GetFiles(_settings.StoragePath, $"{sopInstanceUid}.dcm", SearchOption.AllDirectories)
                : Array.Empty<string>();
            return matches.Length > 0;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitmentSCP", ex, "校验实例归档状态失败 - SOPInstanceUID: {SopInstanceUid}", sopInstanceUid);
            return false;
        }
    }

    private DicomNActionResponse CreateActionResponse(DicomNActionRequest request)
    {
        // 默认响应会自动从请求回显 AffectedSOPClassUID / AffectedSOPInstanceUID 与 MessageIDBeingRespondedTo
        return new DicomNActionResponse(request, DicomStatus.Success);
    }

    private async Task PushEventReportAsync(
        string transactionUid,
        List<ReferencedSopInstance> referenced,
        List<ReferencedSopInstance> failed,
        string host,
        int port,
        string callingAe)
    {
        try
        {
            var eventType = failed.Count == 0 ? EventReportSuccess : EventReportFailuresExist;

            var status = failed.Count == 0 ? StorageCommitmentStatus.Success : StorageCommitmentStatus.FailuresExist;
            await _commitmentRepository.UpdateTransactionResultAsync(transactionUid, status, failed);

            DicomLogger.Information("StorageCommitmentSCP", "推送存储承诺结果 - Transaction: {TransactionUid}, 事件类型: {EventType}, 失败: {FailedCount}/{TotalCount}",
                transactionUid, eventType, failed.Count, referenced.Count);

            // EventReport 数据集：TransactionUID + ReferencedSOPSequence
            var dataset = new DicomDataset
            {
                { DicomTag.TransactionUID, transactionUid }
            };
            var outSequence = new DicomSequence(DicomTag.ReferencedSOPSequence);
            foreach (var item in referenced)
            {
                var itemDs = new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, item.SopClassUid },
                    { DicomTag.ReferencedSOPInstanceUID, item.SopInstanceUid }
                };
                if (!item.Verified)
                {
                    itemDs.Add(DicomTag.FailureReason, item.FailureReason == 0 ? FailureReasonNoSuchObjectInstance : item.FailureReason);
                }
                outSequence.Items.Add(itemDs);
            }
            dataset.Add(outSequence);

            var request = new DicomNEventReportRequest(
                DicomUID.StorageCommitmentPushModel,
                DicomUID.StorageCommitmentPushModelInstance,
                eventType)
            {
                Dataset = dataset
            };

            await SendEventReportWithRetryAsync(host, port, callingAe, request);
            DicomLogger.Information("StorageCommitmentSCP", "存储承诺事件推送成功 - Transaction: {TransactionUid}, 目标: {Host}:{Port} {Ae}",
                transactionUid, host, port, callingAe);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("StorageCommitmentSCP", ex, "推送存储承诺事件失败 - Transaction: {TransactionUid}", transactionUid);
        }
    }

    private async Task SendEventReportWithRetryAsync(string host, int port, string callingAe, DicomNEventReportRequest request)
    {
        var retryCount = Math.Max(1, _settings.StorageCommitmentSCP.PushRetryCount);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                var client = DicomClientFactory.Create(
                    host,
                    port,
                    false,
                    _settings.StorageCommitmentSCP.AeTitle,
                    callingAe);

                using var done = new SemaphoreSlim(0, 1);
                client.NegotiateAsyncOps();

                request.OnResponseReceived += (req, response) =>
                {
                    if (response.Status == DicomStatus.Success)
                    {
                        DicomLogger.Information("StorageCommitmentSCP", "收到事件报告响应 - 状态: {Status}", response.Status);
                    }
                    else
                    {
                        DicomLogger.Warning("StorageCommitmentSCP", "事件报告响应非成功 - 状态: {Status}", response.Status);
                    }
                    done.Release();
                };

                await client.AddRequestAsync(request);
                await client.SendAsync();

                // 等待响应（避免关联提前释放）
                await done.WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("StorageCommitmentSCP", "推送事件报告失败（第 {Attempt}/{Retry} 次）- 目标: {Host}:{Port}, 错误: {Error}",
                    attempt, retryCount, host, port, ex.Message);
                if (attempt == retryCount)
                {
                    throw;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }
}