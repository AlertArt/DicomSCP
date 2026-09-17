using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;
using System.Text.Json;

namespace DicomSCP.Services;

/// <summary>
/// 执行程序步骤服务（Modality Performed Procedure Step SCP）：
/// 接收检查设备的 N-CREATE / N-SET 请求，持久化检查执行过程信息。
/// 参照 PS3.4 Annex F 实现。
/// </summary>
public class MppsSCP : DicomService, IDicomServiceProvider, IDicomNServiceProvider, IDicomCEchoProvider
{
    private static DicomSettings? _globalSettings;
    private static MppsRepository? _globalRepository;

    private readonly DicomSettings _settings;
    private readonly MppsRepository _repository;

    // 支持的传输语法
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = new[]
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    public static void Configure(DicomSettings settings, MppsRepository repository)
    {
        _globalSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _globalRepository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public MppsSCP(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        _settings = _globalSettings ?? throw new InvalidOperationException("MppsSCP is not configured");
        _repository = _globalRepository ?? throw new InvalidOperationException("MppsSCP is not configured");
    }

    // ── 关联处理 ────────────────────────────────────────────────────────────

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("MppsSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                association.CalledAE, association.CallingAE);

            var rejectReason = AssociationGuard.Validate(
                association,
                _settings.MppsSCP.AeTitle,
                _settings.MppsSCP.ValidateCallingAE,
                _settings.MppsSCP.AllowedCallingAEs ?? Array.Empty<string>());

            if (rejectReason.HasValue)
            {
                DicomLogger.Warning("MppsSCP", "拒绝关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}, 原因: {Reason}",
                    association.CalledAE, association.CallingAE, rejectReason);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    rejectReason.Value);
            }

            var supportedSOPClasses = new DicomUID[]
            {
                DicomUID.ModalityPerformedProcedureStep,
                DicomUID.ModalityPerformedProcedureStepRetrieve,
                DicomUID.Verification // C-ECHO
            };

            var hasValidPresentationContext = false;
            foreach (var pc in association.PresentationContexts)
            {
                if (!supportedSOPClasses.Contains(pc.AbstractSyntax))
                {
                    DicomLogger.Warning("MppsSCP", "不支持的服务类型：{AbstractSyntax}", pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                    continue;
                }

                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                hasValidPresentationContext = true;
            }

            if (!hasValidPresentationContext)
            {
                DicomLogger.Warning("MppsSCP", "没有有效的表示上下文，拒绝关联请求");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.ApplicationContextNotSupported);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("MppsSCP", ex, "处理关联请求时发生错误");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.ApplicationContextNotSupported);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        DicomLogger.Information("MppsSCP", "收到关联释放请求");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("MppsSCP", "收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            DicomLogger.Error("MppsSCP", exception, "连接异常关闭");
        }
        else
        {
            DicomLogger.Information("MppsSCP", "连接正常关闭");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("MppsSCP", "收到 C-ECHO 请求");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    // ── N-CREATE ────────────────────────────────────────────────────────────

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        try
        {
            if (request.SOPClassUID != DicomUID.ModalityPerformedProcedureStep)
            {
                DicomLogger.Warning("MppsSCP", "不支持的 SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
                return new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported);
            }

            var mppsId = request.SOPInstanceUID?.UID ?? string.Empty;
            if (string.IsNullOrEmpty(mppsId) || request.Dataset == null)
            {
                return new DicomNCreateResponse(request, DicomStatus.InvalidArgumentValue);
            }

            DicomLogger.Information("MppsSCP", "收到 N-CREATE（检查开始）- MppsId: {MppsId}, Calling AE: {CallingAE}",
                mppsId, Association.CallingAE);

            var record = BuildRecord(mppsId, request.Dataset, create: true);
            await _repository.InsertOrUpdateAsync(record);

            DicomLogger.Information("MppsSCP", "MPPS 已保存 - 状态: {Status}, 检查描述: {Description}",
                record.PerformedProcedureStepStatus, record.PerformedProcedureStepDescription ?? string.Empty);

            return CreateCreateResponse(request);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("MppsSCP", ex, "处理 N-CREATE 请求失败");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // ── N-SET ───────────────────────────────────────────────────────────────

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        try
        {
            if (request.SOPClassUID != DicomUID.ModalityPerformedProcedureStep)
            {
                DicomLogger.Warning("MppsSCP", "不支持的 SOP Class: {SopClass}", request.SOPClassUID?.Name ?? "Unknown");
                return new DicomNSetResponse(request, DicomStatus.SOPClassNotSupported);
            }

            var mppsId = request.SOPInstanceUID?.UID ?? string.Empty;
            if (string.IsNullOrEmpty(mppsId) || request.Dataset == null)
            {
                return new DicomNSetResponse(request, DicomStatus.InvalidArgumentValue);
            }

            DicomLogger.Information("MppsSCP", "收到 N-SET（检查更新）- MppsId: {MppsId}, Calling AE: {CallingAE}",
                mppsId, Association.CallingAE);

            var existing = await _repository.GetAsync(mppsId);
            var record = BuildRecord(mppsId, request.Dataset, create: existing == null);
            await _repository.InsertOrUpdateAsync(record);

            DicomLogger.Information("MppsSCP", "MPPS 已更新 - 状态: {Status}", record.PerformedProcedureStepStatus);

            return CreateSetResponse(request);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("MppsSCP", ex, "处理 N-SET 请求失败");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    // ── 其余 N 服务 ─────────────────────────────────────────────────────────

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        return Task.FromResult(new DicomNActionResponse(request, DicomStatus.SOPClassNotSupported));
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

    private MppsRecord BuildRecord(string mppsId, DicomDataset dataset, bool create)
    {
        var scheduledStep = dataset.TryGetSequence(DicomTag.ScheduledStepAttributesSequence, out var scheduledSeq)
            ? scheduledSeq?.Items.FirstOrDefault()
            : null;

        var record = new MppsRecord
        {
            MppsId = mppsId,
            PerformedProcedureStepId = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepID, string.Empty) ?? string.Empty,
            PerformedProcedureStepStatus = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepStatus, "IN PROGRESS") ?? "IN PROGRESS",
            PerformedProcedureStepStartDate = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepStartDate, string.Empty),
            PerformedProcedureStepStartTime = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepStartTime, string.Empty),
            PerformedProcedureStepEndDate = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepEndDate, string.Empty),
            PerformedProcedureStepEndTime = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepEndTime, string.Empty),
            PerformedProcedureStepDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureStepDescription, string.Empty),
            PerformedProcedureTypeDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedProcedureTypeDescription, string.Empty),
            PerformedStationAeTitle = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedStationAETitle, string.Empty),
            PerformedStationName = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedStationName, string.Empty),
            PerformedLocation = dataset.GetSingleValueOrDefault<string>(DicomTag.PerformedLocation, string.Empty),
            CallingAE = Association.CallingAE,
            CreateTime = create ? DateTime.Now : DateTime.MinValue
        };

        var discontinuation = dataset.TryGetSequence(DicomTag.PerformedProcedureStepDiscontinuationReasonCodeSequence, out var discSeq)
            && discSeq?.Items.FirstOrDefault() != null
            ? discSeq.Items.First()
            : null;
        if (discontinuation != null)
        {
            var codeMeaning = discontinuation.GetSingleValueOrDefault<string>(DicomTag.CodeMeaning, string.Empty);
            var codeValue = discontinuation.GetSingleValueOrDefault<string>(DicomTag.CodeValue, string.Empty);
            record.PerformedProcedureStepDiscontinuationReason = string.IsNullOrEmpty(codeMeaning) ? codeValue : codeMeaning;
        }

        if (scheduledStep != null)
        {
            record.Modality = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
            record.StudyInstanceUid = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
            record.AccessionNumber = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty);
            record.PatientName = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);
            record.PatientId = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty);
            record.PatientBirthDate = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.PatientBirthDate, string.Empty);
            record.PatientSex = scheduledStep.GetSingleValueOrDefault<string>(DicomTag.PatientSex, string.Empty);
        }

        record.Series = ExtractSeries(dataset);
        return record;
    }

    private static List<MppsSeriesRecord> ExtractSeries(DicomDataset dataset)
    {
        var result = new List<MppsSeriesRecord>();
        if (!dataset.TryGetSequence(DicomTag.PerformedSeriesSequence, out var seriesSequence))
        {
            return result;
        }

        foreach (var item in seriesSequence.Items)
        {
            var referencedSops = new List<string>();
            if (item.TryGetSequence(DicomTag.ReferencedImageSequence, out var imageSeq))
            {
                foreach (var refItem in imageSeq.Items)
                {
                    var uid = refItem.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                    if (!string.IsNullOrEmpty(uid)) referencedSops.Add(uid);
                }
            }
            if (item.TryGetSequence(DicomTag.ReferencedNonImageCompositeSOPInstanceSequence, out var nonImageSeq))
            {
                foreach (var refItem in nonImageSeq.Items)
                {
                    var uid = refItem.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                    if (!string.IsNullOrEmpty(uid)) referencedSops.Add(uid);
                }
            }

            result.Add(new MppsSeriesRecord
            {
                SeriesInstanceUid = item.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty),
                Modality = item.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty),
                SeriesDescription = item.GetSingleValueOrDefault<string>(DicomTag.SeriesDescription, string.Empty),
                ProtocolName = item.GetSingleValueOrDefault<string>(DicomTag.ProtocolName, string.Empty),
                PerformingPhysicianName = item.GetSingleValueOrDefault<string>(DicomTag.PerformingPhysicianName, string.Empty),
                OperatorName = item.GetSingleValueOrDefault<string>(DicomTag.OperatorsName, string.Empty),
                ReferencedSopUids = referencedSops.Count > 0 ? JsonSerializer.Serialize(referencedSops) : null
            });
        }

        return result;
    }

    private DicomNCreateResponse CreateCreateResponse(DicomNCreateRequest request)
    {
        var response = new DicomNCreateResponse(request, DicomStatus.Success);
        if (!string.IsNullOrEmpty(request.SOPInstanceUID?.UID))
        {
            response.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID.UID);
        }
        return response;
    }

    private DicomNSetResponse CreateSetResponse(DicomNSetRequest request)
    {
        var response = new DicomNSetResponse(request, DicomStatus.Success);
        if (!string.IsNullOrEmpty(request.SOPInstanceUID?.UID))
        {
            response.Command.AddOrUpdate(DicomTag.AffectedSOPInstanceUID, request.SOPInstanceUID.UID);
        }
        return response;
    }
}