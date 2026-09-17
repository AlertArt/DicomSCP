using FellowOakDicom;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// 模态工作列表客户端（MWL SCU）测试：验证查询数据集构造与响应解析（纯单元测试）。
/// </summary>
public class MwlScuTests
{
    private static MwlScu CreateScu(params RemoteNode[] nodes)
    {
        var config = new QueryRetrieveConfig
        {
            LocalAeTitle = "MWLSCU",
            RemoteNodes = nodes.ToList()
        };
        return new MwlScu(Options.Create(config));
    }

    [Fact]
    public async Task QueryAsync_UnsupportedType_Throws()
    {
        var scu = CreateScu(new RemoteNode
        {
            Name = "STORE",
            AeTitle = "STORESCP",
            HostName = "127.0.0.1",
            Port = 11112,
            Type = "store"
        });

        await Assert.ThrowsAsync<ArgumentException>(() => scu.QueryAsync("store"));
    }

    [Fact]
    public async Task VerifyConnection_UnknownNode_Throws()
    {
        var scu = CreateScu();

        var result = await scu.VerifyConnectionAsync("missing");
        Assert.False(result);
    }

    [Fact]
    public void BuildQueryDataset_IncludesMatchKeysAndReturnKeys()
    {
        var dataset = MwlScu.BuildQueryDataset(new MwlQueryCriteria
        {
            PatientId = "PAT001",
            PatientName = "Zhang^San",
            Modality = "CT",
            ScheduledStationAeTitle = "CTSCANNER",
            ScheduledStartDate = "20240101",
            ScheduledStartTime = "090000",
            AccessionNumber = "ACC001",
            RequestedProcedureId = "RP001"
        });

        // 匹配键
        Assert.Equal("PAT001", dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty));
        Assert.Equal("CT", dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty));
        Assert.Equal("CTSCANNER", dataset.GetSingleValueOrDefault<string>(DicomTag.ScheduledStationAETitle, string.Empty));
        Assert.Equal("ACC001", dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty));
        Assert.Equal("RP001", dataset.GetSingleValueOrDefault<string>(DicomTag.RequestedProcedureID, string.Empty));
        Assert.Contains("Zhang^San", dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty));

        // 时间是单个 Matching Key 值
        Assert.Equal("090000", dataset.GetSingleValueOrDefault<string>(DicomTag.ScheduledProcedureStepStartTime, string.Empty));

        // 返回键
        Assert.True(dataset.Contains(DicomTag.PatientBirthDate));
        Assert.True(dataset.Contains(DicomTag.StudyInstanceUID));
        Assert.True(dataset.Contains(DicomTag.ScheduledProcedureStepDescription));
        Assert.True(dataset.Contains(DicomTag.RequestingPhysician));
        Assert.True(dataset.Contains(DicomTag.ScheduledPerformingPhysicianName));
        Assert.True(dataset.Contains(DicomTag.AdmissionID));
    }

    [Fact]
    public void BuildQueryDataset_NoCriteria_ProducesReturnKeysOnly()
    {
        var dataset = MwlScu.BuildQueryDataset(new MwlQueryCriteria());

        // 未指定条件时匹配键均为空值
        Assert.Equal(string.Empty, dataset.GetString(DicomTag.PatientID));
        Assert.Equal(string.Empty, dataset.GetString(DicomTag.Modality));
        Assert.Equal(string.Empty, dataset.GetString(DicomTag.ScheduledStationAETitle));
        Assert.Equal(string.Empty, dataset.GetString(DicomTag.AccessionNumber));

        // 返回键齐备
        Assert.True(dataset.Contains(DicomTag.PatientName));
        Assert.True(dataset.Contains(DicomTag.PatientSex));
        Assert.True(dataset.Contains(DicomTag.StudyDate));
        Assert.True(dataset.Contains(DicomTag.BodyPartExamined));
    }

    [Fact]
    public void ParseResponse_MapsKnownFields()
    {
        var dataset = new DicomDataset
        {
            { DicomTag.PatientName, "Zhang^San" },
            { DicomTag.PatientID, "PAT001" },
            { DicomTag.PatientBirthDate, "19900101" },
            { DicomTag.PatientSex, "M" },
            { DicomTag.Modality, "CT" },
            { DicomTag.ScheduledStationAETitle, "CTSCANNER" },
            { DicomTag.ScheduledProcedureStepStartDate, "20240101" },
            { DicomTag.ScheduledProcedureStepStartTime, "093000" },
            { DicomTag.StudyInstanceUID, "1.2.3.4.5.6" },
            { DicomTag.AccessionNumber, "ACC001" }
        };

        var item = MwlScu.ParseResponse(dataset);

        Assert.Equal("Zhang^San", item.PatientName);
        Assert.Equal("PAT001", item.PatientId);
        Assert.Equal("19900101", item.PatientBirthDate);
        Assert.Equal("M", item.PatientSex);
        Assert.Equal("CT", item.Modality);
        Assert.Equal("CTSCANNER", item.ScheduledStationAeTitle);
        Assert.Equal("20240101", item.ScheduledProcedureStepStartDate);
        Assert.Equal("093000", item.ScheduledProcedureStepStartTime);
        Assert.Equal("1.2.3.4.5.6", item.StudyInstanceUid);
        Assert.Equal("ACC001", item.AccessionNumber);
        Assert.NotNull(item.DatasetJson);
    }

    [Fact]
    public void ParseResponse_MissingAttributes_ReturnsNullFields()
    {
        var dataset = new DicomDataset();

        var item = MwlScu.ParseResponse(dataset);

        Assert.Null(item.PatientName);
        Assert.Null(item.Modality);
        Assert.Null(item.StudyInstanceUid);
    }
}