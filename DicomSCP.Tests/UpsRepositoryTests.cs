using DicomSCP.Models;
using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// 统一程序步骤（UPS）工作项仓储测试。
/// </summary>
public class UpsRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly UpsRepository _repository;

    public UpsRepositoryTests()
    {
        _repository = new UpsRepository(_db.Config);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static UpsWorkItem MakeItem(string uid, string state = "SCHEDULED")
    {
        return new UpsWorkItem
        {
            SopInstanceUid = uid,
            WorkItemLabel = "CT-001",
            ProcedureStepState = state,
            Priority = "MEDIUM",
            Modality = "CT",
            ScheduledAeTitle = "CTSCANNER",
            ScheduledStartDate = "20240101",
            ScheduledStartTime = "093000",
            PatientName = "Zhang^San",
            PatientId = "PAT001",
            AccessionNumber = "ACC001",
            RequestedProcedureId = "RP001",
            CalledAeTitle = "UPSSCP",
            CallingAe = "WORKSTATION",
            DatasetJson = "{}"
        };
    }

    [Fact]
    public async Task InsertAndGet_RoundTrips()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var inserted = await _repository.InsertOrUpdateAsync(MakeItem("1.2.3.4.20.1"));
        Assert.True(inserted);

        var fetched = await _repository.GetAsync("1.2.3.4.20.1");
        Assert.NotNull(fetched);
        Assert.Equal("CT-001", fetched.WorkItemLabel);
        Assert.Equal("SCHEDULED", fetched.ProcedureStepState);
        Assert.Equal("CTSCANNER", fetched.ScheduledAeTitle);
        Assert.Equal("PAT001", fetched.PatientId);
    }

    [Fact]
    public async Task Update_OverwritesState()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.InsertOrUpdateAsync(MakeItem("1.2.3.4.20.2", state: "SCHEDULED"));
        await _repository.InsertOrUpdateAsync(MakeItem("1.2.3.4.20.2", state: "IN PROGRESS"));

        var fetched = await _repository.GetAsync("1.2.3.4.20.2");
        Assert.NotNull(fetched);
        Assert.Equal("IN PROGRESS", fetched.ProcedureStepState);
    }

    [Fact]
    public async Task Query_FiltersByStateAndModality()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.InsertOrUpdateAsync(MakeItem("1.2.3.4.20.3", state: "SCHEDULED"));
        await _repository.InsertOrUpdateAsync(MakeItem("1.2.3.4.20.4", state: "COMPLETED"));

        var scheduled = await _repository.QueryAsync(status: "SCHEDULED");
        Assert.Single(scheduled);
        Assert.Equal("1.2.3.4.20.3", scheduled[0].SopInstanceUid);
    }

    [Fact]
    public async Task Delete_RemovesItem()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.InsertOrUpdateAsync(MakeItem("1.2.3.4.20.5"));
        var deleted = await _repository.DeleteAsync("1.2.3.4.20.5");
        Assert.True(deleted);

        Assert.Null(await _repository.GetAsync("1.2.3.4.20.5"));
    }
}