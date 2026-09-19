using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Imaging.Codec;
using DicomSCP.Configuration;
using DicomSCP.Repository;
using DicomSCP.Models;
using System.Collections.Concurrent;  // 添加这个引用

namespace DicomSCP.Services;

public partial class QRSCP : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider, IDicomCMoveProvider, IDicomCGetProvider, IDicomCStoreProvider
{
    private static DicomSettings? _globalSettings;
    private static DicomRepository? _globalRepository;

    // 接受语法统一由 DicomNegotiation 提供（原为与本类及 CStoreSCP 重复的硬编码列表）
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes = DicomNegotiation.SupportedBasicSyntaxes;

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes = DicomNegotiation.SupportedImageStorageSyntaxes;

    private readonly DicomSettings _settings;
    private readonly DicomRepository _repository;
    private bool _associationReleaseLogged = false;
    private bool _transferSyntaxLogged = false;

    // 用于跟踪每个目标 AE 的发送任务
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDestinations = new();

    public static void Configure(DicomSettings settings, DicomRepository repository)
    {
        _globalSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _globalRepository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public QRSCP(
        INetworkStream stream, 
        Encoding fallbackEncoding, 
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
        try
        {
            _settings = _globalSettings ?? throw new InvalidOperationException("QRSCP is not configured");
            _repository = _globalRepository ?? throw new InvalidOperationException("QRSCP is not configured");
            DicomLogger.Information("QRSCP", "QR服务初始化完成");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "QR服务初始化失");
            throw;
        }
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        try
        {
            DicomLogger.Information("QRSCP", "收到关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}", 
                association.CalledAE, association.CallingAE);

            // 统一关联校验：应用上下文 + Called/Calling AE（见 AssociationGuard）
            var rejectReason = AssociationGuard.Validate(
                association,
                _settings?.QRSCP.AeTitle ?? string.Empty,
                _settings?.QRSCP.ValidateCallingAE == true,
                _settings?.QRSCP?.AllowedCallingAEs ?? Enumerable.Empty<string>());

            if (rejectReason.HasValue)
            {
                DicomLogger.Warning("QRSCP", "拒绝关联请求 - Called AE: {CalledAE}, Calling AE: {CallingAE}, 原因: {Reason}",
                    association.CalledAE, association.CallingAE, rejectReason);
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    rejectReason.Value);
            }

            DicomLogger.Information("QRSCP", "验证通过 - Called AE: {CalledAE}, Calling AE: {CallingAE}",
                association.CalledAE, association.CallingAE);

            var storageCount = 0;
            foreach (var pc in association.PresentationContexts)
            {
                // 检查是否支持请求的服务
                if (pc.AbstractSyntax == DicomUID.Verification ||                                // C-ECHO
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelFind || // C-FIND
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelFind || // C-FIND (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove || // C-MOVE
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove || // C-MOVE (Patient Root)
                    pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||  // C-GET
                    pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet || // C-GET (Patient Root)
                    pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)             // Storage (for C-GET)
                {
                    // 记录服务接受日志
                    if (pc.AbstractSyntax.StorageCategory == DicomStorageCategory.None)
                    {
                        DicomLogger.Information("QRSCP", "接受服务 - AET: {CallingAE}, 服务: {Service}", 
                            association.CallingAE, pc.AbstractSyntax.Name);
                    }

                    // 根据服务类型选择合适的传输语法
                    if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                    {
                        // 对于存储类服务（C-GET需要），接受所有支持的传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                    }
                    else if (pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelGet ||
                             pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                             pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove)
                    {
                        // 对于 C-GET/C-MOVE 服务，同时接受基本传输语法和图像传输语法
                        pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes.Concat(AcceptedTransferSyntaxes).Distinct().ToArray());
                        DicomLogger.Information("QRSCP", "为C-GET/C-MOVE服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
                    }
                    else
                    {
                        // 对于其他服务，使用基本传输语法
                        pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                        DicomLogger.Information("QRSCP", "为基本服务接受传输语法 - 服务: {Service}", pc.AbstractSyntax.Name);
                    }
                }
                else
                {
                    DicomLogger.Warning("QRSCP", "拒绝不支持的服务 - AET: {CallingAE}, 服务: {Service}", 
                        association.CallingAE, pc.AbstractSyntax.Name);
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            // 如果有存储类服务，只记录一条汇总日志
            if (storageCount > 0)
            {
                DicomLogger.Information("QRSCP", "接受存储类服务 - AET: {CallingAE}, 支持的存储类数: {Count}", 
                    association.CallingAE, storageCount);
            }

            return SendAssociationAcceptAsync(association);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "处理关联请求失败");
            return SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
        }
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        if (!_associationReleaseLogged)
        {
            DicomLogger.Information("QRSCP", "接收到关联释放请求");
            _associationReleaseLogged = true;
        }
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        DicomLogger.Warning("QRSCP", "接收到中止请求 - 来源: {Source}, 原因: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        try
        {
            if (exception != null)
            {
                DicomLogger.Error("QRSCP", exception, "连接异常关闭");
            }

            // 重置状态
            _associationReleaseLogged = false;
            _transferSyntaxLogged = false;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "处理连接关闭失败");
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-ECHO 请求 - 来自: {CallingAE}", Association.CallingAE);
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        DicomLogger.Information("QRSCP", "收到 C-FIND 请求 - 来自: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        if (request.Level != DicomQueryRetrieveLevel.Study && 
            request.Level != DicomQueryRetrieveLevel.Series && 
            request.Level != DicomQueryRetrieveLevel.Image &&
            request.Level != DicomQueryRetrieveLevel.Patient)
        {
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        // 使 fo-dicom 的查询理
        var responses = request.Level switch
        {
            DicomQueryRetrieveLevel.Patient => await HandlePatientLevelFind(request),
            DicomQueryRetrieveLevel.Study => await HandleStudyLevelFind(request),
            DicomQueryRetrieveLevel.Series => await HandleSeriesLevelFind(request),
            DicomQueryRetrieveLevel.Image => await HandleImageLevelFind(request),
            _ => new List<DicomCFindResponse>()
        };

        foreach (var response in responses)
        {
            yield return response;
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

}
