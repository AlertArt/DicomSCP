using DicomSCP.Configuration;
using DicomSCP.Models;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;

namespace DicomSCP.Services;

/// <summary>
/// 存储承诺 N-EVENT-REPORT 构建与推送（含重试），供 SCP 首次推送与失败重推复用。
/// </summary>
public static class StorageCommitmentNotifier
{
    // N-EVENT-REPORT 事件类型：1 = 成功，2 = 存在失败
    public const ushort EventReportSuccess = 1;
    public const ushort EventReportFailuresExist = 2;

    // FailureReason：0112 = No Such Object Instance（实例未被归档）
    public const ushort FailureReasonNoSuchObjectInstance = 0x0112;
    public const ushort FailureReasonProcessingFailure = 0x0110;

    public static ushort ResolveEventType(IEnumerable<ReferencedSopInstance> referenced) =>
        referenced.Any(r => !r.Verified) ? EventReportFailuresExist : EventReportSuccess;

    /// <summary>构建 N-EVENT-REPORT 数据集：TransactionUID + ReferencedSOPSequence（含失败原因）。</summary>
    public static DicomDataset BuildEventReportDataset(string transactionUid, IEnumerable<ReferencedSopInstance> referenced)
    {
        var dataset = new DicomDataset
        {
            { DicomTag.TransactionUID, transactionUid }
        };

        var sequence = new DicomSequence(DicomTag.ReferencedSOPSequence);
        foreach (var item in referenced)
        {
            var itemDs = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, item.SopClassUid },
                { DicomTag.ReferencedSOPInstanceUID, item.SopInstanceUid }
            };
            if (!item.Verified)
            {
                itemDs.Add(DicomTag.FailureReason,
                    item.FailureReason == 0 ? FailureReasonNoSuchObjectInstance : item.FailureReason);
            }
            sequence.Items.Add(itemDs);
        }
        dataset.Add(sequence);
        return dataset;
    }

    /// <summary>在新关联上推送 N-EVENT-REPORT（带重试）。返回是否成功与最后的错误信息。</summary>
    public static async Task<(bool Sent, string? Error)> SendAsync(
        DicomSettings settings,
        string host,
        int port,
        string callingAe,
        ushort eventType,
        DicomDataset dataset)
    {
        var retryCount = Math.Max(1, settings.StorageCommitmentSCP.PushRetryCount);
        string? lastError = null;

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                var request = new DicomNEventReportRequest(
                    DicomUID.StorageCommitmentPushModel,
                    DicomUID.StorageCommitmentPushModelInstance,
                    eventType)
                {
                    Dataset = dataset
                };

                var client = DicomClientFactory.Create(
                    host, port, false, settings.StorageCommitmentSCP.AeTitle, callingAe);

                using var done = new SemaphoreSlim(0, 1);
                client.NegotiateAsyncOps();
                request.OnResponseReceived += (_, _) => done.Release();

                await client.AddRequestAsync(request);
                await client.SendAsync();

                // 等待响应，避免关联提前释放
                await done.WaitAsync(TimeSpan.FromSeconds(10));
                return (true, null);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                DicomLogger.Warning("StorageCommitment", "推送事件报告失败（第 {Attempt}/{Retry} 次）- 目标: {Host}:{Port}, 错误: {Error}",
                    attempt, retryCount, host, port, ex.Message);

                if (attempt < retryCount)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        return (false, lastError);
    }
}
