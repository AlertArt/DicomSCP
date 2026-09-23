using System.Collections.Concurrent;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;

namespace DicomSCP.Services;

public partial class QRSCP
{
    public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
    {
        DicomMetrics.Increment(DicomMetrics.CMove);
        var destinationAE = request.DestinationAE;

        // 检查目标 AE 是否正在处理中
        if (_activeDestinations.TryGetValue(destinationAE, out var existingCts))
        {
            DicomLogger.Warning("QRSCP", "目标AE有未完成的任务，取消旧任务 - AET: {AET}", destinationAE);
            existingCts.Cancel();
            existingCts.Dispose();
            _activeDestinations.TryRemove(destinationAE, out _);
        }

        // 创建新的取消令牌
        var cts = new CancellationTokenSource();
        _activeDestinations.TryAdd(destinationAE, cts);

        try
        {
            DicomLogger.Information("QRSCP", "收到C-MOVE请求 - AE: {CallingAE}, 目标: {DestinationAE}", 
                Association.CallingAE, request.DestinationAE);

            // 1. 验证目标 AE
            var moveDestination = _settings.QRSCP.MoveDestinations
                .FirstOrDefault(x => x.AeTitle.Equals(request.DestinationAE, StringComparison.OrdinalIgnoreCase));

            if (moveDestination == null)
            {
                DicomLogger.Warning("QRSCP", "目标AE未配置 - AET: {AET}", request.DestinationAE);
                yield return new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
                yield break;
            }

            // 2. 测试目标 SCP 连接
            var client = CreateDicomClient(moveDestination);
            DicomResponse? response = null;
            try 
            {
                var echoRequest = new DicomCEchoRequest();
                await client.AddRequestAsync(echoRequest);
                await client.SendAsync();
                DicomLogger.Information("QRSCP", "目标SCP连接测试成功 - AET: {AET}", request.DestinationAE);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "目标SCP连接测试失败 - AET: {AET}", request.DestinationAE);
                response = new DicomCMoveResponse(request, DicomStatus.QueryRetrieveMoveDestinationUnknown);
            }

            if (response != null)
            {
                yield return (DicomCMoveResponse)response;
                yield break;
            }

            // 3. 获取实例列表
            var instances = await GetRequestedInstances(request);
            if (!instances.Any())
            {
                DicomLogger.Information("QRSCP", "未找到匹配实例");
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
                yield break;
            }

            // 4. 处理实例传输并返回进度
            var totalInstances = instances.Count();
            var result = await ProcessInstances(request, instances, client, cts.Token);

            // 5. 返回进度和最终状态
            yield return new DicomCMoveResponse(request, DicomStatus.Pending)
            {
                Dataset = CreateProgressResponse(
                    request,
                    totalInstances,
                    result.successCount,
                    result.failedCount).Dataset
            };

            var finalStatus = result.hasNetworkError ? DicomStatus.QueryRetrieveMoveDestinationUnknown :
                             result.failedCount > 0 ? DicomStatus.ProcessingFailure :
                             DicomStatus.Success;

            yield return new DicomCMoveResponse(request, finalStatus)
            {
                Dataset = CreateProgressResponse(
                    request,
                    totalInstances,
                    result.successCount,
                    result.failedCount,
                    finalStatus).Dataset
            };

            // 添加完成日志
            DicomLogger.Information("QRSCP", 
                "C-MOVE完成 - 总数: {Total}, 成功: {Success}, 失败: {Failed}, 状态: {Status}", 
                totalInstances, 
                result.successCount, 
                result.failedCount, 
                finalStatus);
        }
        finally
        {
            // 清理资源
            if (_activeDestinations.TryRemove(destinationAE, out var currentCts) && currentCts == cts)
            {
                cts.Dispose();
            }
        }
    }


    private async Task<(int successCount, int failedCount, bool hasNetworkError)> SendBatch(
        IDicomClient client, 
        List<DicomFile> files, 
        int currentSuccess, 
        int currentFailed)
    {
        try
        {
            // 批量转码
            var requestedTransferSyntax = GetRequestedTransferSyntax(files[0]);
            if (requestedTransferSyntax != null)
            {
                files = TranscodeFilesIfNeeded(files, requestedTransferSyntax);
            }

            await SendToDestinationAsync(client, files);
            return (currentSuccess, currentFailed, false);
        }
        catch (DicomNetworkException ex)
        {
            DicomLogger.Error("QRSCP", ex, "网络错误，停止发送");
            return (currentSuccess, currentFailed + files.Count, true);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "发送失败");
            return (currentSuccess, currentFailed + files.Count, false);
        }
    }


    private DicomResponse CreateProgressResponse(
        DicomRequest request, 
        int totalInstances, 
        int successCount, 
        int failedCount,
        DicomStatus? status = null)
    {
        var remaining = totalInstances - (successCount + failedCount);
        var currentStatus = status ?? (remaining > 0 ? DicomStatus.Pending : 
            failedCount > 0 ? DicomStatus.ProcessingFailure : DicomStatus.Success);

        var dataset = new DicomDataset()
            .Add(DicomTag.NumberOfRemainingSuboperations, (ushort)remaining)
            .Add(DicomTag.NumberOfCompletedSuboperations, (ushort)successCount)
            .Add(DicomTag.NumberOfFailedSuboperations, (ushort)failedCount);

        return request switch
        {
            DicomCGetRequest getRequest => new DicomCGetResponse(getRequest, currentStatus) { Dataset = dataset },
            DicomCMoveRequest moveRequest => new DicomCMoveResponse(moveRequest, currentStatus) { Dataset = dataset },
            _ => throw new ArgumentException("Unsupported request type", nameof(request))
        };
    }


    private IDicomClient CreateDicomClient(MoveDestination destination)
    {
        var client = DicomClientFactory.Create(
            destination.HostName,
            destination.Port,
            false,
            _settings.QRSCP.AeTitle,
            destination.AeTitle);

        client.NegotiateAsyncOps();

        // 添加所有可能的存储类 PresentationContexts
        // （奇数 PC ID，符合 PS3.8 9.3.2；原实现 1..14 含偶数 ID）
        var storageUids = new DicomUID[]
        {
            DicomUID.CTImageStorage,
            DicomUID.MRImageStorage,
            DicomUID.UltrasoundImageStorage,
            DicomUID.SecondaryCaptureImageStorage,
            DicomUID.XRayAngiographicImageStorage,
            DicomUID.XRayRadiofluoroscopicImageStorage,
            DicomUID.DigitalXRayImageStorageForPresentation,
            DicomUID.DigitalMammographyXRayImageStorageForPresentation,
            DicomUID.UltrasoundMultiFrameImageStorage,
            DicomUID.EnhancedCTImageStorage,
            DicomUID.EnhancedMRImageStorage,
            DicomUID.EnhancedXAImageStorage,
            DicomUID.NuclearMedicineImageStorage,
            DicomUID.PositronEmissionTomographyImageStorage
        };

        foreach (var pc in DicomNegotiation.ComputeMissingPresentationContexts(
                     Enumerable.Empty<DicomPresentationContext>(),
                     storageUids,
                     AcceptedImageTransferSyntaxes))
        {
            client.AdditionalPresentationContexts.Add(pc);
        }

        return client;
    }


}
