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

    // 实现 IDicomCStoreProvider 接口
    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        DicomLogger.Warning("QRSCP", "不支持直接存储服务 - AET: {CallingAE}, SOPInstanceUID: {SopInstanceUid}", 
            Association?.CallingAE ?? "Unknown",
            request.SOPInstanceUID?.ToString() ?? string.Empty);

        // 返回不支持的状态
        return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.SOPClassNotSupported));
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        DicomLogger.Warning("QRSCP", e, "存储请求异常 - 临时文件: {TempFile}", 
            tempFileName ?? string.Empty);
        return Task.CompletedTask;
    }


}
