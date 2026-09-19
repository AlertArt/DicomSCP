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
    private bool IsNetworkError(Exception ex)
    {
        if (ex == null) return false;

        // 检查是否是网络相关的异常
        return ex is System.Net.Sockets.SocketException ||
               ex is System.Net.WebException ||
               ex is System.IO.IOException ||
               ex is DicomNetworkException ||
               (ex.InnerException != null && IsNetworkError(ex.InnerException));
    }


    private DicomTransferSyntax? GetRequestedTransferSyntax(DicomFile file)
    {
        try
        {
            // 1. 从 C-MOVE/C-GET 服务的 Presentation Context 中获取传输语法
            var moveContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == DicomUID.StudyRootQueryRetrieveInformationModelMove ||
                                   pc.AbstractSyntax == DicomUID.PatientRootQueryRetrieveInformationModelMove);

            var moveTransferSyntax = moveContext?.AcceptedTransferSyntax;
            if (moveTransferSyntax != null)
            {
                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP", 
                        "传输语法检查 - 本地文件: {SourceSyntax}, 请求格式: {TargetSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        moveTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return moveTransferSyntax;
            }

            // 2. 获取当前文件的 SOP Class UID 对应的传输语法
            var sopClassUid = file.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
            var storageContext = Association.PresentationContexts
                .FirstOrDefault(pc => pc.AbstractSyntax == sopClassUid);

            var storageTransferSyntax = storageContext?.AcceptedTransferSyntax;
            if (storageTransferSyntax != null)
            {
                if (!_transferSyntaxLogged)
                {
                    DicomLogger.Information("QRSCP", 
                        "存储类传输语法检查 - 本地文件: {SourceSyntax}, 请求格式: {TargetSyntax}", 
                        file.Dataset.InternalTransferSyntax.UID.Name,
                        storageTransferSyntax.UID.Name);
                    _transferSyntaxLogged = true;
                }

                return storageTransferSyntax;
            }

            // 3. 如果都没有指定传输语法，使用默认的传输语法
            DicomLogger.Information("QRSCP", "未指定传输语法，使用默认传输语法: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取请求的传输语法失败，使用默认传输语法: Explicit VR Little Endian");
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
    }

    private List<DicomFile> TranscodeFilesIfNeeded(List<DicomFile> files, DicomTransferSyntax targetSyntax)
    {
        // 1. 快速检查是否需要转码
        if (files.All(f => f.Dataset.InternalTransferSyntax == targetSyntax))
        {
            return files;
        }

        var transcoder = new DicomTranscoder(DicomTransferSyntax.ExplicitVRLittleEndian, targetSyntax);
        var results = new ConcurrentBag<DicomFile>();  // 使用线程安全的集合

        // 2. 并行转码
        Parallel.ForEach(files, 
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            file => 
        {
            try
            {
                if (file.Dataset.InternalTransferSyntax != targetSyntax)
                {
                    var transcodedFile = transcoder.Transcode(file);
                    results.Add(transcodedFile);
                }
                else
                {
                    results.Add(file);
                }
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("QRSCP", ex, "语法转换失败，使用原格式");
                results.Add(file);
            }
        });

        return results.ToList();
    }


    private const int BatchSize = 10;  // 固定批量大小


    private async Task<(int successCount, int failedCount, bool hasNetworkError)> ProcessInstances(
        DicomRequest request,
        IEnumerable<Instance> instances,
        IDicomClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var totalInstances = instances.Count();
        var successCount = 0;
        var failedCount = 0;
        var hasNetworkError = false;

        foreach (var seriesGroup in instances.GroupBy(x => x.SeriesInstanceUid))
        {
            // 检查是否已取消
            if (cancellationToken.IsCancellationRequested)
            {
                DicomLogger.Warning("QRSCP", "任务已取消，停止处理");
                break;
            }

            var batchFiles = new List<DicomFile>();
            try
            {
                foreach (var instance in seriesGroup)
                {
                    // 如果已经发生网络错误或取消，立即停止处理
                    if (hasNetworkError || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                        if (!File.Exists(filePath))
                        {
                            failedCount++;
                            continue;
                        }

                        var file = await DicomFile.OpenAsync(filePath);
                        batchFiles.Add(file);

                        // 达到批次大小时处理
                        if (batchFiles.Count >= BatchSize)
                        {
                            // 检查是否已取消
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            // 发送批次
                            var result = client != null 
                                ? await SendBatch(client, batchFiles, successCount, failedCount)
                                : await SendLocalBatch(request, batchFiles, successCount, failedCount);

                            if (result.hasNetworkError)
                            {
                                hasNetworkError = true;
                                failedCount = result.failedCount;
                                break;
                            }

                            successCount += batchFiles.Count;
                            failedCount = result.failedCount;
                            batchFiles.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        DicomLogger.Error("QRSCP", ex, "处理失败 - UID: {SopInstanceUid}", instance.SopInstanceUid);
                    }
                }

                // 如果已经发生网络错误或取消，不处理剩余文件
                if (!hasNetworkError && !cancellationToken.IsCancellationRequested && batchFiles.Any())
                {
                    // 发送批次
                    var result = client != null 
                        ? await SendBatch(client, batchFiles, successCount, failedCount)
                        : await SendLocalBatch(request, batchFiles, successCount, failedCount);

                    if (result.hasNetworkError)
                    {
                        hasNetworkError = true;
                        failedCount = result.failedCount;
                    }
                    else
                    {
                        successCount += batchFiles.Count;
                        failedCount = result.failedCount;
                    }
                }
            }
            finally
            {
                batchFiles.Clear();
            }

            // 如果发生网络错误或取消，立即停止处理下一个序列
            if (hasNetworkError || cancellationToken.IsCancellationRequested)
            {
                DicomLogger.Warning("QRSCP", 
                    cancellationToken.IsCancellationRequested ? "任务已取消，停止处理" : "发现网络错误，停止处理");
                break;
            }
        }

        return (successCount, failedCount, hasNetworkError);
    }

    // C-GET 本地发送
    private async Task<(int successCount, int failedCount, bool hasNetworkError)> SendLocalBatch(
        DicomRequest request,
        List<DicomFile> files,
        int currentSuccess,
        int currentFailed)
    {
        try
        {
            // 1. 批量转码
            var requestedTransferSyntax = GetRequestedTransferSyntax(files[0]);
            if (requestedTransferSyntax != null)
            {
                files = TranscodeFilesIfNeeded(files, requestedTransferSyntax);
            }

            // 2. 逐个发送
            foreach (var file in files)
            {
                await SendRequestAsync(new DicomCStoreRequest(file));  // 使用基类的 SendRequestAsync
            }
            return (currentSuccess + files.Count, currentFailed, false);
        }
        catch (Exception ex)
        {
            if (IsNetworkError(ex))
            {
                DicomLogger.Error("QRSCP", ex, "网络错误，停止发送");
                return (currentSuccess, currentFailed + files.Count, true);
            }
            
            DicomLogger.Error("QRSCP", ex, "发送失败");
            return (currentSuccess, currentFailed + files.Count, false);
        }
    }

    private async Task SendToDestinationAsync(IDicomClient client, List<DicomFile> files)
    {
        // 1. 幂等添加缺失的存储类 PresentationContext：
        //    原实现每批次按 Count() 分配 ID（首批复制 ID 14、含偶数、跨批重复累积、
        //    超过 255 时 (byte) 截断回绕）；现仅补充尚未覆盖的抽象语法，奇数 ID 不回绕
        var newContexts = DicomNegotiation.ComputeMissingPresentationContexts(
            client.AdditionalPresentationContexts,
            files.GroupBy(f => f.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID)).Select(g => g.Key),
            AcceptedImageTransferSyntaxes);

        foreach (var presentationContext in newContexts)
        {
            DicomLogger.Information("QRSCP", "新增表示上下文 - ID: {PcId}, SOP Class: {SopClass}",
                presentationContext.ID, presentationContext.AbstractSyntax.Name);
            client.AdditionalPresentationContexts.Add(presentationContext);
        }

        // 2. 批量添加请求
        foreach (var file in files)
        {
            await client.AddRequestAsync(new DicomCStoreRequest(file));
        }

        // 3. 一次性发送所有请求
        await client.SendAsync();
    }

}
