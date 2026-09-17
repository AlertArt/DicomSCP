using DicomSCP.Models;
using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// 执行程序步骤（MPPS）仓储测试。
/// </summary>
public class MppsRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly MppsRepository _repository;

    public MppsRepositoryTests()
    {
        _repository = new MppsRepository(_db.Config);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static MppsRecord MakeRecord(string mppsId, string status = "IN PROGRESS")
    {
        return new MppsRecord
        {
            MppsId = mppsId,
            PerformedProcedureStepId = "PPS-001",
            PerformedProcedureStepStatus = status,
            PerformedProcedureStepStartDate = "20240101",
            PerformedProcedureStepStartTime = "093000",
            PerformedProcedureStepDescription = "CT 胸部检查",
            PerformedStationAeTitle = "CTSCANNER",
            Modality = "CT",
            StudyInstanceUid = "1.2.3.4.10.1",
            PatientName = "Zhang^San",
            PatientId = "PAT001",
            CallingAE = "CTSCANNER",
            Series = new List<MppsSeriesRecord>
            {
                new()
                {
                    SeriesInstanceUid = "1.2.3.4.10.2",
                    Modality = "CT",
                    SeriesDescription = "胸部平扫",
                    ProtocolName = "CHEST",
                    ReferencedSopUids = "[\"1.2.3.4.10.3\"]"
                }
            }
        };
    }

    [Fact]
    public async Task InsertAndGet_RoundTripsWithSeries()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var record = MakeRecord("1.2.3.4.10.100");
        var inserted = await _repository.InsertOrUpdateAsync(record);
        Assert.True(inserted);

        var fetched = await _repository.GetAsync("1.2.3.4.10.100");
        Assert.NotNull(fetched);
        Assert.Equal("IN PROGRESS", fetched.PerformedProcedureStepStatus);
        Assert.Equal("CT 胸部检查", fetched.PerformedProcedureStepDescription);
        Assert.Equal("PAT001", fetched.PatientId);
        Assert.Single(fetched.Series);
        Assert.Equal("1.2.3.4.10.2", fetched.Series[0].SeriesInstanceUid);
        Assert.Contains("1.2.3.4.10.3", fetched.Series[0].ReferencedSopUids);
    }

    [Fact]
    public async Task Update_OverwritesExistingRecord()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.InsertOrUpdateAsync(MakeRecord("1.2.3.4.10.101", status: "IN PROGRESS"));
        await _repository.InsertOrUpdateAsync(MakeRecord("1.2.3.4.10.101", status: "COMPLETED"));

        var fetched = await _repository.GetAsync("1.2.3.4.10.101");
        Assert.NotNull(fetched);
        Assert.Equal("COMPLETED", fetched.PerformedProcedureStepStatus);
    }

    [Fact]
    public async Task GetRecent_FilterByStatus()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.InsertOrUpdateAsync(MakeRecord("1.2.3.4.10.201", status: "IN PROGRESS"));
        await _repository.InsertOrUpdateAsync(MakeRecord("1.2.3.4.10.202", status: "COMPLETED"));

        var completed = await _repository.GetRecentAsync(status: "COMPLETED");
        Assert.Single(completed);
        Assert.Equal("1.2.3.4.10.202", completed[0].MppsId);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var fetched = await _repository.GetAsync("9.9.9.9.9");

        Assert.Null(fetched);
    }
}