using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Serialization;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;

namespace DicomSCP.Services;

/// <summary>
/// 统一程序步骤（Unified Procedure Step SCP）：
/// 接收 UPS 工作项的 N-CREATE / N-SET / N-GET / N-DELETE / N-ACTION，并向订阅者推送
/// N-EVENT-REPORT 状态变更通知。
/// 参照 PS3.4 Annex KK（UPS Push Model / Watch Model）实现。
/// 注：通用用途工作清单（General Purpose Worklist，RETIRED）已被 UPS 取代，本服务不再实现。
/// </summary>
public class UpsSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private static DicomSettings? _globalSettings;
    private static UpsRepository? _globalRepository;

    private readonly DicomSettings _settings;
    private readonly UpsRepository _repository;

    private static readonly ConcurrentDictionary<string /*upsSopInstanceUid*/, List<UpsSubscriber>> SubscribersByInstance = new();
    private static readonly ConcurrentBag<UpsSubscriber> GlobalSubscribers = new();

    // UPS 事件类型（PS3.4 KK.4.1.1）
    private const int EventTypeRealWorldProcedureInstanceDeleted = 1;
    private const int EventTypeUpsStateChanged = 2;

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    public static void Configure(DicomSettings settings, UpsRepository repository)
    {
        _globalSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _globalRepository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public UpsSCP(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = _globalSettings ?? throw new InvalidOperationException("UpsSCP is not configured");
        _repository = _globalRepository ?? throw new InvalidOperationException("UpsSCP is not configured");
    }

    // ── 关联处理 ────────────────────────────────────────────────────────────

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("UpsSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                association.CalledAE, association.CallingAE);

            var rejectReason = AssociationGuard.Validate(
                association,
                _settings.UpsSCP.AeTitle,
                _settings.UpsSCP.ValidateCallingAE,
                _settings.UpsSCP.AllowedCallingAEs ?? Array.Empty<string>());

            if (rejectReason.HasValue)
            {
                DicomLogger.Warning("UpsSCP", "拒绝关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}, 原因: {Reason}",
                    association.CalledAE, association.CallingAE, rejectReason);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    rejectReason.Value);
            }

            var supportedSOPClasses = new DicomUID[]
            {
                DicomUID.UnifiedProcedureStepPush,
                DicomUID.UnifiedProcedureStepWatch,
                DicomUID.UnifiedProcedureStepPull,
                DicomUID.UnifiedProcedureStepEvent,
                DicomUID.UnifiedProcedureStepQuery,
                DicomUID.Verification // C-ECHO
            };

            var hasValidPresentationContext = false;
            foreach (var pc in association.PresentationContexts)
            {
                if (!supportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    DicomLogger.Warning("UpsSCP", "不支持的服务类型：{AbstractSyntax}", pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    continue;
                }

                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                hasValidPresentationContext = true;
            }

            if (!hasValidPresentationContext)
            {
                DicomLogger.Warning("UpsSCP", "没有有效的表示上下文，拒绝关联请求");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.ApplicationContextNotSupported);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.ApplicationContextNotSupported);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("UpsSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("UpsSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("UpsSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("UpsSCP", "连接正常关闭");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("UpsSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    // ── N-CREATE ────────────────────────────────────────────────────────────

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            if (request.SOPClassUID != DicomUID.UnifiedProcedureStepPush)
            {
                DicomLogger.Warning("UpsSCP", "N-CREATE 不支持的 SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
                return new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported);
            }

            if (request.Dataset == null)
            {
                return new DicomNCreateResponse(request, DicomStatus.InvalidArgumentValue);
            }

            // 类型1：ProcedureStepState (0074,1000)
            var state = request.Dataset.GetSingleValueOrDefault<string>(new DicomTag(0x0074, 0x1000), string.Empty);
            if (string.IsNullOrEmpty(state))
            {
                DicomLogger.Warning("UpsSCP", "N-CREATE 缺少 ProcedureStepState 属性");
                return new DicomNCreateResponse(request, DicomStatus.MissingAttribute);
            }

            if (!string.Equals(state, "SCHEDULED", StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("UpsSCP", "N-CREATE 状态非法（应为 SCHEDULED）: {State}", state);
                return new DicomNCreateResponse(request, DicomStatus.InvalidAttributeValue);
            }

            var sopInstanceUid = string.IsNullOrEmpty(request.SOPInstanceUID?.UID)
                ? DicomUIDGenerator.GenerateDerivedFromUUID().UID
                : request.SOPInstanceUID.UID;

            DicomLogger.Information("UpsSCP", "收到 N-CREATE - UPS: {SopInstanceUid}, Calling AE: {CallingAE}",
                sopInstanceUid, Association.CallingAE);

            var item = BuildItem(sopInstanceUid, request.Dataset, create: true);
            await _repository.InsertOrUpdateAsync(item);

            // 通知全局订阅者新工作项就绪
            _ = Task.Run(() => PushStateChangedNotificationAsync(item));

            DicomLogger.Information("UpsSCP", "UPS 工作项已创建 - {SopInstanceUid}", sopInstanceUid);

            var response = new DicomNCreateResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.UnifiedProcedureStepPush },
                { DicomTag.CommandField, (ushort)0x8141 }, // N-CREATE-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 }, // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, sopInstanceUid }
            };
            SetCommandDataset(response, command);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // ── N-SET ───────────────────────────────────────────────────────────────

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            if (request.SOPClassUID != DicomUID.UnifiedProcedureStepPush)
            {
                DicomLogger.Warning("UpsSCP", "N-SET 不支持的 SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
                return new DicomNSetResponse(request, DicomStatus.SOPClassNotSupported);
            }

            var sopInstanceUid = request.SOPInstanceUID?.UID ?? string.Empty;
            if (string.IsNullOrEmpty(sopInstanceUid) || request.Dataset == null)
            {
                return new DicomNSetResponse(request, DicomStatus.InvalidArgumentValue);
            }

            var existing = await _repository.GetAsync(sopInstanceUid);
            if (existing == null)
            {
                DicomLogger.Warning("UpsSCP", "N-SET 目标不存在 - UPS: {SopInstanceUid}", sopInstanceUid);
                return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            // 合并数据集：将请求中的顶层属性覆盖存储值
            var original = RestoreDataset(existing);
            foreach (var item in request.Dataset)
            {
                original.AddOrUpdate(item);
            }
            var already = original.GetSingleValueOrDefault<string>(new DicomTag(0x0074, 0x1000), string.Empty);

            var updated = BuildItem(sopInstanceUid, original, create: false);
            await _repository.InsertOrUpdateAsync(updated);

            DicomLogger.Information("UpsSCP", "UPS 已更新 - {SopInstanceUid}, 状态: {State}", sopInstanceUid,
                string.IsNullOrEmpty(already) ? "-" : already);

            _ = Task.Run(() => PushStateChangedNotificationAsync(updated));

            var response = new DicomNSetResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.UnifiedProcedureStepPush },
                { DicomTag.CommandField, (ushort)0x8121 }, // N-SET-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 }, // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, sopInstanceUid }
            };
            SetCommandDataset(response, command);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "处理 N-SET 请求失败");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // ── N-GET ───────────────────────────────────────────────────────────────

    public async Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        try
        {
            var sopInstanceUid = request.SOPInstanceUID?.UID ?? string.Empty;
            if (string.IsNullOrEmpty(sopInstanceUid))
            {
                return new DicomNGetResponse(request, DicomStatus.InvalidArgumentValue);
            }

            var existing = await _repository.GetAsync(sopInstanceUid);
            if (existing == null)
            {
                DicomLogger.Warning("UpsSCP", "N-GET 目标不存在 - UPS: {SopInstanceUid}", sopInstanceUid);
                return new DicomNGetResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            var dataset = RestoreDataset(existing);
            if (request.Attributes is { Length: > 0 })
            {
                var filtered = new DicomDataset();
                foreach (var tag in request.Attributes)
                {
                    if (dataset.Contains(tag))
                    {
                        filtered.AddOrUpdate(dataset.GetDicomItem<DicomItem>(tag));
                    }
                }
                dataset = filtered;
            }

            var response = new DicomNGetResponse(request, DicomStatus.Success)
            {
                Dataset = dataset
            };
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.UnifiedProcedureStepPush },
                { DicomTag.CommandField, (ushort)0x8122 }, // N-GET-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0002 }, // 有数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, sopInstanceUid }
            };
            SetCommandDataset(response, command);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "处理 N-GET 请求失败");
            return new DicomNGetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // ── N-DELETE ────────────────────────────────────────────────────────────

    public async Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        try
        {
            var sopInstanceUid = request.SOPInstanceUID?.UID ?? string.Empty;
            if (string.IsNullOrEmpty(sopInstanceUid))
            {
                return new DicomNDeleteResponse(request, DicomStatus.InvalidArgumentValue);
            }

            var existing = await _repository.GetAsync(sopInstanceUid);
            if (existing == null)
            {
                DicomLogger.Warning("UpsSCP", "N-DELETE 目标不存在 - UPS: {SopInstanceUid}", sopInstanceUid);
                return new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            var state = existing.ProcedureStepState ?? string.Empty;
            if (!string.Equals(state, "SCHEDULED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, "DELETED", StringComparison.OrdinalIgnoreCase))
            {
                // 已开始/已完成的步骤不可删除（PS3.4 KK.8）
                DicomLogger.Warning("UpsSCP", "N-DELETE 状态不允许删除 - UPS: {SopInstanceUid}, 状态: {State}",
                    sopInstanceUid, state);
                return new DicomNDeleteResponse(request, DicomStatus.ClassInstanceConflict);
            }

            var deleted = await _repository.DeleteAsync(sopInstanceUid);
            if (!deleted)
            {
                return new DicomNDeleteResponse(request, DicomStatus.NoSuchObjectInstance);
            }

            DicomLogger.Information("UpsSCP", "UPS 已删除 - {SopInstanceUid}", sopInstanceUid);

            var response = new DicomNDeleteResponse(request, DicomStatus.Success);
            var command = new DicomDataset
            {
                { DicomTag.AffectedSOPClassUID, DicomUID.UnifiedProcedureStepPush },
                { DicomTag.CommandField, (ushort)0x8123 }, // N-DELETE-RSP
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.CommandDataSetType, (ushort)0x0101 }, // 无数据集
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.AffectedSOPInstanceUID, sopInstanceUid }
            };
            SetCommandDataset(response, command);
            return response;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "处理 N-DELETE 请求失败");
            return new DicomNDeleteResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // ── N-ACTION ────────────────────────────────────────────────────────────

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        try
        {
            var actionType = request.ActionTypeID;
            var targetUid = request.SOPInstanceUID?.UID ?? string.Empty;

            DicomLogger.Information("UpsSCP", "收到 N-ACTION - 目标: {Target}, 动作: {ActionType}, Calling AE: {CallingAE}",
                targetUid, actionType, Association.CallingAE);

            switch (actionType)
            {
                case 1: // Subscribe（订阅 UPS 状态变更通知）
                    return await SubscribeAsync(request, targetUid);
                case 2: // Cancel Request（请求取消 UPS）
                    return await CancelRequestAsync(request, targetUid);
                case 3: // Change UPS State（请求状态变更）
                    return await ChangeUpsStateAsync(request, targetUid);
                default:
                    DicomLogger.Warning("UpsSCP", "N-ACTION 不支持的动作类型: {ActionType}", actionType);
                    return new DicomNActionResponse(request, DicomStatus.NoSuchActionType);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "处理 N-ACTION 请求失败");
            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task<DicomNActionResponse> SubscribeAsync(DicomNActionRequest request, string targetUid)
    {
        if (string.IsNullOrEmpty(targetUid))
        {
            return new DicomNActionResponse(request, DicomStatus.InvalidArgumentValue);
        }

        var subscriber = new UpsSubscriber
        {
            AeTitle = Association.CallingAE,
            Host = Association.RemoteHost,
            Port = Association.RemotePort,
            RemEmail = GetNullable(request.Dataset, new DicomTag(0x0040, 0x4070))
        };

        // 全局订阅：目标为众所周知的 Watch/Pull 实例或不在当前仓储中
        if (await _repository.GetAsync(targetUid) == null)
        {
            GlobalSubscribers.Add(subscriber);
            DicomLogger.Information("UpsSCP", "注册全局 UPS 订阅 - AE: {AeTitle}, 地址: {Host}:{Port}",
                subscriber.AeTitle, subscriber.Host, subscriber.Port);
        }
        else
        {
            var list = SubscribersByInstance.GetOrAdd(targetUid, _ => new List<UpsSubscriber>());
            list.Add(subscriber);
            DicomLogger.Information("UpsSCP", "注册 UPS 订阅 - {UpsUid}, AE: {AeTitle}, 地址: {Host}:{Port}",
                targetUid, subscriber.AeTitle, subscriber.Host, subscriber.Port);
        }

        return CreateActionSuccessResponse(request, targetUid);
    }

    private async Task<DicomNActionResponse> CancelRequestAsync(DicomNActionRequest request, string targetUid)
    {
        var item = await _repository.GetAsync(targetUid);
        if (item == null)
        {
            return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
        }

        var dataset = RestoreDataset(item);
        dataset.AddOrUpdate(new DicomTag(0x0074, 0x1000), "CANCELED");
        var updated = BuildItem(targetUid, dataset, create: false);
        await _repository.InsertOrUpdateAsync(updated);

        DicomLogger.Information("UpsSCP", "UPS 已取消 - {SopInstanceUid}", targetUid);
        _ = Task.Run(() => PushStateChangedNotificationAsync(updated));

        return CreateActionSuccessResponse(request, targetUid);
    }

    private async Task<DicomNActionResponse> ChangeUpsStateAsync(DicomNActionRequest request, string targetUid)
    {
        var item = await _repository.GetAsync(targetUid);
        if (item == null)
        {
            return new DicomNActionResponse(request, DicomStatus.NoSuchObjectInstance);
        }

        var newState = request.Dataset?.GetSingleValueOrDefault<string>(new DicomTag(0x0074, 0x1000), string.Empty);
        if (string.IsNullOrEmpty(newState))
        {
            return new DicomNActionResponse(request, DicomStatus.MissingAttribute);
        }

        var current = item.ProcedureStepState?.ToUpperInvariant() ?? string.Empty;
        newState = newState.ToUpperInvariant();

        // 简单的状态迁移约束
        var allowed = (current, newState) switch
        {
            ("SCHEDULED", "IN PROGRESS") => true,
            ("SCHEDULED", "CANCELED") => true,
            ("IN PROGRESS", "COMPLETED") => true,
            ("IN PROGRESS", "CANCELED") => true,
            ("SCHEDULED", "COMPLETED") => true,
            ("CANCELED", "SCHEDULED") => true,
            _ => false
        };

        if (!allowed)
        {
            DicomLogger.Warning("UpsSCP", "非法状态迁移 - {UpsUid}: {Current} -> {New}",
                targetUid, current, newState);
            return new DicomNActionResponse(request, DicomStatus.AttributeValueOutOfRange);
        }

        var dataset = RestoreDataset(item);
        dataset.AddOrUpdate(new DicomTag(0x0074, 0x1000), newState);
        var updated = BuildItem(targetUid, dataset, create: false);
        await _repository.InsertOrUpdateAsync(updated);

        DicomLogger.Information("UpsSCP", "UPS 状态变更 - {SopInstanceUid}: {Current} -> {New}",
            targetUid, current, newState);
        _ = Task.Run(() => PushStateChangedNotificationAsync(updated));

        return CreateActionSuccessResponse(request, targetUid);
    }

    private DicomNActionResponse CreateActionSuccessResponse(DicomNActionRequest request, string targetUid)
    {
        var response = new DicomNActionResponse(request, DicomStatus.Success);
        var command = new DicomDataset
        {
            { DicomTag.AffectedSOPClassUID, DicomUID.UnifiedProcedureStepWatch },
            { DicomTag.CommandField, (ushort)0x8131 }, // N-ACTION-RSP
            { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
            { DicomTag.CommandDataSetType, (ushort)0x0101 }, // 无数据集
            { DicomTag.Status, (ushort)DicomStatus.Success.Code },
            { DicomTag.AffectedSOPInstanceUID, targetUid }
        };
        SetCommandDataset(response, command);
        return response;
    }

    // ── N-EVENT-REPORT（入站） ───────────────────────────────────────────────

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        DicomLogger.Information("UpsSCP", "收到 N-EVENT-REPORT 请求 - EventTypeID: {EventType}, SOP: {Sop}",
            request.EventTypeID, request.SOPInstanceUID?.UID ?? string.Empty);
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private UpsWorkItem BuildItem(string sopInstanceUid, DicomDataset dataset, bool create)
    {
        var state = dataset.GetSingleValueOrDefault<string>(new DicomTag(0x0074, 0x1000), string.Empty);
        var station = GetNullable(dataset, DicomTag.ScheduledStationAETitle);
        if (string.IsNullOrEmpty(station))
        {
            // 也尝试从 Scheduled Procedure Step Attributes 序列中读取
            var seq = dataset.GetSequence(DicomTag.ScheduledStepAttributesSequence);
            station = GetNullable(seq?.Items.FirstOrDefault(), DicomTag.ScheduledStationAETitle);
        }

        var patientName = GetNullable(dataset, DicomTag.PatientName);
        var patientId = GetNullable(dataset, DicomTag.PatientID);
        if (string.IsNullOrEmpty(patientName) || string.IsNullOrEmpty(patientId))
        {
            // 患者信息可能位于 RequestAttributes 序列中
            var refSeq = dataset.GetSequence(new DicomTag(0x0040, 0x0275))?
                .Items.FirstOrDefault()?
                .GetSequence(DicomTag.RequestAttributesSequence)?
                .Items.FirstOrDefault();
            if (refSeq != null)
            {
                patientName = GetNullable(refSeq, DicomTag.PatientName);
                patientId = GetNullable(refSeq, DicomTag.PatientID);
            }
        }

        var item = new UpsWorkItem
        {
            SopInstanceUid = sopInstanceUid,
            WorkItemLabel = GetNullable(dataset, DicomTag.WorklistLabel),
            ProcedureStepState = string.IsNullOrEmpty(state) ? null : state.ToUpperInvariant(),
            Priority = GetNullable(dataset, DicomTag.ScheduledProcedureStepPriority),
            Modality = GetNullable(dataset, DicomTag.Modality),
            ScheduledAeTitle = station,
            ScheduledStartDate = GetNullable(dataset, DicomTag.ScheduledProcedureStepStartDate),
            ScheduledStartTime = GetNullable(dataset, DicomTag.ScheduledProcedureStepStartTime),
            PatientName = patientName,
            PatientId = patientId,
            AccessionNumber = GetNullable(dataset, DicomTag.AccessionNumber),
            RequestedProcedureId = GetNullable(dataset, DicomTag.RequestedProcedureID),
            CalledAeTitle = Association.CalledAE,
            CallingAe = Association.CallingAE,
            DatasetJson = DicomJson.ConvertDicomToJson(dataset),
            CreateTime = create ? DateTime.Now : DateTime.MinValue
        };
        return item;
    }

    private static string? GetNullable(DicomDataset? dataset, DicomTag tag)
    {
        if (dataset == null)
        {
            return null;
        }
        var value = dataset.GetSingleValueOrDefault<string>(tag, string.Empty);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DicomDataset RestoreDataset(UpsWorkItem item)
    {
        if (!string.IsNullOrEmpty(item.DatasetJson))
        {
            return DicomJson.ConvertJsonToDicom(item.DatasetJson, true);
        }
        return new DicomDataset();
    }

    private async Task PushStateChangedNotificationAsync(UpsWorkItem item)
    {
        try
        {
            var specific = SubscribersByInstance.TryGetValue(item.SopInstanceUid, out var list)
                ? list.ToList()
                : new List<UpsSubscriber>();
            var global = GlobalSubscribers.ToList();
            var subscribers = specific.Concat(global).DistinctBy(s => (s.AeTitle, s.Host, s.Port)).ToList();

            if (subscribers.Count == 0)
            {
                return;
            }

            var dataset = RestoreDataset(item);
            foreach (var subscriber in subscribers)
            {
                await PushEventReportWithRetryAsync(subscriber, item.SopInstanceUid, dataset);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "推送 UPS 状态变更通知失败 - {SopInstanceUid}", item.SopInstanceUid);
        }
    }

    private async Task PushEventReportWithRetryAsync(UpsSubscriber subscriber, string sopInstanceUid, DicomDataset dataset)
    {
        var retryCount = Math.Max(1, _settings.UpsSCP.PushRetryCount);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                var upsInstanceUid = new DicomUID(sopInstanceUid, "Unified Procedure Step Instance", DicomUidType.SOPInstance);
                var request = new DicomNEventReportRequest(
                    DicomUID.UnifiedProcedureStepEvent,
                    upsInstanceUid,
                    EventTypeUpsStateChanged)
                {
                    Dataset = dataset
                };

                var client = DicomClientFactory.Create(
                    subscriber.Host,
                    subscriber.Port,
                    false,
                    _settings.UpsSCP.AeTitle,
                    subscriber.AeTitle);

                using var done = new SemaphoreSlim(0, 1);
                client.NegotiateAsyncOps();

                request.OnResponseReceived += (req, response) =>
                {
                    if (response.Status == DicomStatus.Success)
                    {
                        DicomLogger.Information("UpsSCP", "UPS 事件通知已确认 - {SopInstanceUid} -> {Ae}@{Host}:{Port}",
                            sopInstanceUid, subscriber.AeTitle, subscriber.Host, subscriber.Port);
                    }
                    else
                    {
                        DicomLogger.Warning("UpsSCP", "UPS 事件通知响应非成功 - 状态: {Status}", response.Status);
                    }
                    done.Release();
                };

                await client.AddRequestAsync(request);
                await client.SendAsync();
                await done.WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("UpsSCP", "推送 UPS 事件通知失败（第 {Attempt}/{Retry} 次）- 目标: {Ae}@{Host}:{Port}, 错误: {Error}",
                    attempt, retryCount, subscriber.AeTitle, subscriber.Host, subscriber.Port, ex.Message);
                if (attempt == retryCount)
                {
                    throw;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    private static void SetCommandDataset(DicomResponse response, DicomDataset command)
    {
        try
        {
            var commandProperty = typeof(DicomMessage).GetProperty("Command",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            commandProperty?.SetValue(response, command);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("UpsSCP", ex, "设置命令数据集时发生错误");
        }
    }
}