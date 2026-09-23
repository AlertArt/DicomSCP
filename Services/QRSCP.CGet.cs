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
    public async IAsyncEnumerable<DicomCGetResponse> OnCGetRequestAsync(DicomCGetRequest request)
    {
        DicomMetrics.Increment(DicomMetrics.CGet);
        DicomLogger.Information("QRSCP", "收到C-GET请求 - AE: {CallingAE}, 级别: {Level}", 
            Association.CallingAE, request.Level);

        // 1. 验证请求参数
        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "C-GET缺少StudyInstanceUID");
            yield return new DicomCGetResponse(request, DicomStatus.InvalidAttributeValue);
            yield break;
        }

        // 2. 获取实例列表
        var instances = await GetRequestedInstances(request);
        if (!instances.Any())
        {
            DicomLogger.Information("QRSCP", "未找到匹配实例");
            yield return new DicomCGetResponse(request, DicomStatus.Success);
            yield break;
        }

        // 3. 处理实例传输
        var totalInstances = instances.Count();
        var result = await ProcessInstances(request, instances);
        
        // 4. 只返回最终状态
        var finalStatus = result.hasNetworkError ? DicomStatus.ProcessingFailure :
                         result.failedCount > 0 ? DicomStatus.ProcessingFailure :
                         DicomStatus.Success;

        yield return new DicomCGetResponse(request, finalStatus)
        {
            Dataset = CreateProgressResponse(
                request,
                totalInstances,
                result.successCount,
                result.failedCount,
                finalStatus).Dataset
        };
    }


    private async Task<IEnumerable<Instance>> GetRequestedInstances(DicomRequest request)
    {
        try
        {
            var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
            var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
            var sopInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty);

            DicomLogger.Information("QRSCP", 
                "获取实例列表 - Study: {StudyUid}, Series: {SeriesUid}, SOP: {SopUid}",
                studyInstanceUid, seriesInstanceUid, sopInstanceUid);

            if (!string.IsNullOrEmpty(sopInstanceUid))
            {
                var instance = await Task.Run(() => _repository.GetInstanceAsync(sopInstanceUid));
                return instance != null ? new[] { instance } : Array.Empty<Instance>();
            }
            
            if (!string.IsNullOrEmpty(seriesInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid));
            }
            
            if (!string.IsNullOrEmpty(studyInstanceUid))
            {
                return await Task.Run(() => _repository.GetInstancesByStudyUid(studyInstanceUid));
            }

            DicomLogger.Warning("QRSCP", "请求缺少必要的UID");
            return Array.Empty<Instance>();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "获取实例列表失败");
            return Array.Empty<Instance>();
        }
    }


}
