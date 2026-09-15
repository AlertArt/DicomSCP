using DicomSCP.Models;
using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

public class WorklistRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly WorklistRepository _repository;

    public WorklistRepositoryTests()
    {
        _repository = new WorklistRepository(_db.Config);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static WorklistItem MakeItem(
        string worklistId = "WL-001",
        string patientId = "PAT001",
        string modality = "CT") => new()
    {
        WorklistId = worklistId,
        PatientId = patientId,
        PatientName = "Zhang^San",
        PatientBirthDate = "19800101",
        PatientSex = "M",
        StudyInstanceUid = "1.2.3.4.5.1",
        StudyDescription = "Chest Study",
        Modality = modality,
        ScheduledAET = "CT_STATION_1",
        ScheduledDateTime = "2025-01-15 10:00",
        ScheduledStationName = "CT-01",
        ScheduledProcedureStepID = "SPS-001",
        ScheduledProcedureStepDescription = "Routine CT",
        RequestedProcedureID = "RP-001",
        RequestedProcedureDescription = "CT Chest",
        ReferringPhysicianName = "Wang^Wu",
        Status = "SCHEDULED",
        BodyPartExamined = "CHEST",
        ReasonForRequest = "Checkup",
        AccessionNumber = "ACC-WL-001"
    };

    [Fact]
    public async Task Create_And_GetPaged_Roundtrip()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        await _repository.CreateAsync(MakeItem());

        var page = await _repository.GetPagedAsync(1, 10);

        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal("PAT001", page.Items[0].PatientId);
        Assert.Equal("CT", page.Items[0].Modality);
        Assert.Equal("SCHEDULED", page.Items[0].Status);
    }

    [Fact]
    public async Task GetById_ReturnsCreatedItem()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        await _repository.CreateAsync(MakeItem(worklistId: "WL-FIND-ME"));

        var item = await _repository.GetByIdAsync("WL-FIND-ME");

        Assert.NotNull(item);
        Assert.Equal("Zhang^San", item.PatientName);
        Assert.Equal("CT_STATION_1", item.ScheduledAET);
        Assert.Equal("ACC-WL-001", item.AccessionNumber);
    }

    [Fact]
    public async Task GetPaged_FiltersByModality()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        await _repository.CreateAsync(MakeItem(worklistId: "WL-CT", modality: "CT"));
        await _repository.CreateAsync(MakeItem(worklistId: "WL-MR", patientId: "PAT002", modality: "MR"));

        var ctOnly = await _repository.GetPagedAsync(1, 10, modality: "CT");
        var mrOnly = await _repository.GetPagedAsync(1, 10, modality: "MR");

        Assert.Single(ctOnly.Items);
        Assert.Equal("WL-CT", ctOnly.Items[0].WorklistId);
        Assert.Single(mrOnly.Items);
        Assert.Equal("WL-MR", mrOnly.Items[0].WorklistId);
    }

    [Fact]
    public async Task GetPaged_FiltersByPatientId()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        await _repository.CreateAsync(MakeItem(worklistId: "WL-A", patientId: "PAT001"));
        await _repository.CreateAsync(MakeItem(worklistId: "WL-B", patientId: "PAT002"));

        var result = await _repository.GetPagedAsync(1, 10, patientId: "PAT002");

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("WL-B", result.Items[0].WorklistId);
    }

    [Fact]
    public async Task Update_ChangesModality()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        var item = MakeItem(worklistId: "WL-UPD", modality: "CT");
        await _repository.CreateAsync(item);

        item.Modality = "MR";
        var updated = await _repository.UpdateAsync(item);

        Assert.True(updated);

        var after = await _repository.GetByIdAsync("WL-UPD");
        Assert.NotNull(after);
        Assert.Equal("MR", after.Modality);
    }

    [Fact]
    public async Task Delete_RemovesItem()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        await _repository.CreateAsync(MakeItem(worklistId: "WL-DEL"));

        var deleted = await _repository.DeleteAsync("WL-DEL");

        Assert.True(deleted);
        Assert.Null(await _repository.GetByIdAsync("WL-DEL"));

        var page = await _repository.GetPagedAsync(1, 10);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var deleted = await _repository.DeleteAsync("WL-NOT-EXIST");

        Assert.False(deleted);
    }
}
