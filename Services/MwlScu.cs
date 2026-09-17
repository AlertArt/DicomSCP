using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Serialization;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;
using DicomSCP.Models;

namespace DicomSCP.Services;

public interface IMwlScu
{
    Task<List<MwlWorklistItem>> QueryAsync(string remoteName, MwlQueryCriteria? criteria = null, CancellationToken cancellationToken = default);
    Task<bool> VerifyConnectionAsync(string remoteName, CancellationToken cancellationToken = default);
}

/// <summary>
/// 模态工作列表客户端（Modality Worklist SCU）：
/// 通过 C-FIND 在 Modality Worklist 信息模型上发起查询，参照 PS3.4 Annex K 实现。
/// </summary>
public class MwlScu : IMwlScu
{
    private readonly QueryRetrieveConfig _config;

    public MwlScu(IOptions<QueryRetrieveConfig> config)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    private IDicomClient CreateClient(RemoteNode node)
    {
        var client = DicomClientFactory.Create(
            node.HostName,
            node.Port,
            false,
            _config.LocalAeTitle,
            node.AeTitle);

        client.NegotiateAsyncOps();
        return client;
    }

    private RemoteNode GetRemoteNode(string remoteName)
    {
        var node = _config.RemoteNodes?.FirstOrDefault(n => n.Name.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
        if (node == null)
        {
            throw new ArgumentException($"未找到名为 {remoteName} 的远程节点配置");
        }

        if (node.Type.ToLower() is not ("worklist" or "wl" or "all"))
        {
            throw new ArgumentException($"节点 {remoteName} 不支持工作列表操作（当前类型: {node.Type}）");
        }

        return node;
    }

    public async Task<bool> VerifyConnectionAsync(string remoteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = GetRemoteNode(remoteName);
            DicomLogger.Information("MwlScu", "开始验证连接 - 目标: {Name} ({Host}:{Port} {AE})",
                node.Name, node.HostName, node.Port, node.AeTitle);

            var client = CreateClient(node);
            var verified = false;
            var request = new DicomCEchoRequest();

            request.OnResponseReceived += (req, response) =>
            {
                verified = response.Status == DicomStatus.Success;
                DicomLogger.Information("MwlScu", "收到 C-ECHO 响应 - 状态: {Status}", response.Status);
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            DicomLogger.Information("MwlScu", "连接验证完成 - 结果: {Result}", verified);
            return verified;
        }
        catch (Exception ex)
        {
            DicomLogger.Error("MwlScu", ex, "连接验证失败");
            return false;
        }
    }

    public async Task<List<MwlWorklistItem>> QueryAsync(
        string remoteName,
        MwlQueryCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        var node = GetRemoteNode(remoteName);
        DicomLogger.Information("MwlScu", "开始查询工作列表 - 目标: {Name} ({Host}:{Port} {AE})",
            node.Name, node.HostName, node.Port, node.AeTitle);

        var client = CreateClient(node);
        var request = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind, DicomPriority.Medium)
        {
            Dataset = BuildQueryDataset(criteria ?? new MwlQueryCriteria())
        };

        var items = new List<MwlWorklistItem>();
        DicomStatus? finalStatus = null;

        request.OnResponseReceived += (req, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                if (response.Dataset != null)
                {
                    items.Add(ParseResponse(response.Dataset));
                }
            }
            else
            {
                finalStatus = response.Status;
            }
        };

        await client.AddRequestAsync(request);
        await client.SendAsync(cancellationToken);

        if (finalStatus != null && finalStatus.State == DicomState.Failure)
        {
            DicomLogger.Warning("MwlScu", "工作列表查询失败 - 状态: {Status}", finalStatus);
            throw new DicomNetworkException($"工作列表查询失败: {finalStatus.State} [{finalStatus.Code}: {finalStatus.Description}]");
        }

        DicomLogger.Information("MwlScu", "工作列表查询完成 - 目标: {Name}, 返回条目: {Count}",
            node.Name, items.Count);
        return items;
    }

    internal static DicomDataset BuildQueryDataset(MwlQueryCriteria c)
    {
        var dataset = new DicomDataset();

        // ── 匹配键（带值） ──
        if (!string.IsNullOrEmpty(c.ScheduledStationAeTitle))
        {
            dataset.Add(DicomTag.ScheduledStationAETitle, c.ScheduledStationAeTitle);
        }
        if (!string.IsNullOrEmpty(c.Modality))
        {
            dataset.Add(DicomTag.Modality, c.Modality);
        }
        if (!string.IsNullOrEmpty(c.ScheduledStartDate))
        {
            dataset.Add(DicomTag.ScheduledProcedureStepStartDate, c.ScheduledStartDate);
        }
        if (!string.IsNullOrEmpty(c.ScheduledStartTime))
        {
            dataset.Add(DicomTag.ScheduledProcedureStepStartTime, c.ScheduledStartTime);
        }
        if (!string.IsNullOrEmpty(c.AccessionNumber))
        {
            dataset.Add(DicomTag.AccessionNumber, c.AccessionNumber);
        }
        if (!string.IsNullOrEmpty(c.PatientId))
        {
            dataset.Add(DicomTag.PatientID, c.PatientId);
        }
        if (!string.IsNullOrEmpty(c.PatientName))
        {
            dataset.Add(new DicomPersonName(DicomTag.PatientName, c.PatientName));
        }
        if (!string.IsNullOrEmpty(c.RequestedProcedureId))
        {
            dataset.Add(DicomTag.RequestedProcedureID, c.RequestedProcedureId);
        }
        if (!string.IsNullOrEmpty(c.ReferringPhysician))
        {
            dataset.Add(new DicomPersonName(DicomTag.ReferringPhysicianName, c.ReferringPhysician));
        }

        // ── 返回键（空值） ──
        dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
        AddReturnKey(dataset, DicomTag.PatientName);
        AddReturnKey(dataset, DicomTag.PatientID);
        AddReturnKey(dataset, DicomTag.PatientBirthDate);
        AddReturnKey(dataset, DicomTag.PatientSex);
        AddReturnKey(dataset, DicomTag.AccessionNumber);
        AddReturnKey(dataset, DicomTag.ReferringPhysicianName);
        AddReturnKey(dataset, DicomTag.StudyInstanceUID);
        AddReturnKey(dataset, DicomTag.StudyDate);
        AddReturnKey(dataset, DicomTag.StudyTime);
        AddReturnKey(dataset, DicomTag.StudyDescription);
        AddReturnKey(dataset, DicomTag.Modality);
        AddReturnKey(dataset, DicomTag.RequestedProcedureID);
        AddReturnKey(dataset, DicomTag.RequestedProcedureDescription);
        AddReturnKey(dataset, DicomTag.RequestingPhysician);
        AddReturnKey(dataset, DicomTag.ScheduledStationAETitle);
        AddReturnKey(dataset, DicomTag.ScheduledProcedureStepStartDate);
        AddReturnKey(dataset, DicomTag.ScheduledProcedureStepStartTime);
        AddReturnKey(dataset, DicomTag.ScheduledStationName);
        AddReturnKey(dataset, DicomTag.ScheduledPerformingPhysicianName);
        AddReturnKey(dataset, DicomTag.ScheduledProcedureStepDescription);
        AddReturnKey(dataset, DicomTag.ScheduledProcedureStepID);
        AddReturnKey(dataset, DicomTag.AdmissionID);
        AddReturnKey(dataset, DicomTag.BodyPartExamined);

        return dataset;
    }

    private static void AddReturnKey(DicomDataset dataset, DicomTag tag)
    {
        if (!dataset.Contains(tag))
        {
            dataset.Add(tag, string.Empty);
        }
    }

    internal static MwlWorklistItem ParseResponse(DicomDataset dataset)
    {
        return new MwlWorklistItem
        {
            PatientId = GetString(dataset, DicomTag.PatientID),
            PatientName = GetString(dataset, DicomTag.PatientName),
            PatientBirthDate = GetString(dataset, DicomTag.PatientBirthDate),
            PatientSex = GetString(dataset, DicomTag.PatientSex),
            AccessionNumber = GetString(dataset, DicomTag.AccessionNumber),
            ReferringPhysician = GetString(dataset, DicomTag.ReferringPhysicianName),
            StudyInstanceUid = GetString(dataset, DicomTag.StudyInstanceUID),
            StudyDate = GetString(dataset, DicomTag.StudyDate),
            StudyTime = GetString(dataset, DicomTag.StudyTime),
            StudyDescription = GetString(dataset, DicomTag.StudyDescription),
            Modality = GetString(dataset, DicomTag.Modality),
            RequestedProcedureId = GetString(dataset, DicomTag.RequestedProcedureID),
            RequestedProcedureDescription = GetString(dataset, DicomTag.RequestedProcedureDescription),
            RequestingPhysician = GetString(dataset, DicomTag.RequestingPhysician),
            ScheduledStationAeTitle = GetString(dataset, DicomTag.ScheduledStationAETitle),
            ScheduledProcedureStepStartDate = GetString(dataset, DicomTag.ScheduledProcedureStepStartDate),
            ScheduledProcedureStepStartTime = GetString(dataset, DicomTag.ScheduledProcedureStepStartTime),
            ScheduledStationName = GetString(dataset, DicomTag.ScheduledStationName),
            ScheduledPerformingPhysician = GetString(dataset, DicomTag.ScheduledPerformingPhysicianName),
            ScheduledProcedureStepDescription = GetString(dataset, DicomTag.ScheduledProcedureStepDescription),
            ScheduledProcedureStepId = GetString(dataset, DicomTag.ScheduledProcedureStepID),
            AdmissionId = GetString(dataset, DicomTag.AdmissionID),
            BodyPartExamined = GetString(dataset, DicomTag.BodyPartExamined),
            DatasetJson = DicomJson.ConvertDicomToJson(dataset)
        };
    }

    internal static string? GetString(DicomDataset dataset, DicomTag tag)
    {
        if (!dataset.Contains(tag))
        {
            return null;
        }
        var value = dataset.GetSingleValueOrDefault<string>(tag, string.Empty);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}