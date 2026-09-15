using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

public class DicomDatasetPersistenceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DicomDatasetPersistence _persistence;

    public DicomDatasetPersistenceTests()
    {
        _persistence = new DicomDatasetPersistence(_db.Config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void BuildBatchData_ExtractsFourLevelHierarchy()
    {
        var dataset = DicomTestData.MakeInstance(
            patientId: "PAT100",
            patientName: "Zhang^San",
            studyUid: "1.2.3.4.5.1",
            seriesUid: "1.2.3.4.5.2",
            sopUid: "1.2.3.4.5.3",
            studyDate: "20240515",
            modality: "MR");

        var batch = _persistence.BuildBatchData(
            new List<(FellowOakDicom.DicomDataset Dataset, string FilePath)> { (dataset, "2024/05/15/study/series/file.dcm") },
            DateTime.Now);

        Assert.True(batch.HasData);
        Assert.Single(batch.Patients);
        Assert.Single(batch.Studies);
        Assert.Single(batch.Series);
        Assert.Single(batch.Instances);

        Assert.Equal("PAT100", batch.Patients[0].PatientId);
        Assert.Equal("Zhang^San", batch.Patients[0].PatientName);

        Assert.Equal("1.2.3.4.5.1", batch.Studies[0].StudyInstanceUid);
        Assert.Equal("PAT100", batch.Studies[0].PatientId);
        Assert.Equal("20240515", batch.Studies[0].StudyDate);
        Assert.Equal("MR", batch.Studies[0].Modality);

        Assert.Equal("1.2.3.4.5.2", batch.Series[0].SeriesInstanceUid);
        Assert.Equal("1.2.3.4.5.1", batch.Series[0].StudyInstanceUid);

        Assert.Equal("1.2.3.4.5.3", batch.Instances[0].SopInstanceUid);
        Assert.Equal("2024/05/15/study/series/file.dcm", batch.Instances[0].FilePath);
    }

    [Fact]
    public void BuildBatchData_EmptyInput_HasNoData()
    {
        var batch = _persistence.BuildBatchData(
            new List<(FellowOakDicom.DicomDataset Dataset, string FilePath)>(),
            DateTime.Now);

        Assert.False(batch.HasData);
    }

    [Fact]
    public async Task InsertBatch_WritesRowsQueryableByRepository()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var writeResult = await Seed.InsertAsync(_db, _persistence,
            DicomTestData.MakeInstance(studyUid: "1.2.3.4.100.1", seriesUid: "1.2.3.4.100.2", sopUid: "1.2.3.4.100.3"));

        Assert.Equal(1, writeResult.InsertedPatients);
        Assert.Equal(1, writeResult.InsertedStudies);
        Assert.Equal(1, writeResult.InsertedSeries);
        Assert.Equal(1, writeResult.InsertedInstances);

        var repository = new DicomRepository(_db.Config);
        var studies = repository.GetStudies("", "", "", ("", ""), null, "1.2.3.4.100.1");

        Assert.Single(studies);
        Assert.Equal("1.2.3.4.100.1", studies[0].StudyInstanceUid);

        var series = repository.GetSeriesByStudyUid("1.2.3.4.100.1");
        Assert.Single(series);

        var instances = repository.GetInstancesBySeriesUid("1.2.3.4.100.1", "1.2.3.4.100.2");
        Assert.Single(instances);
        Assert.Equal("1.2.3.4.100.3", instances[0].SopInstanceUid);
        Assert.EndsWith("1.2.3.4.100.3.dcm", instances[0].FilePath);
    }

    [Fact]
    public async Task InsertBatch_DuplicateSopInstance_IsIgnoredByIdempotentInsert()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var dataset = DicomTestData.MakeInstance(
            studyUid: "1.2.3.4.200.1",
            seriesUid: "1.2.3.4.200.2",
            sopUid: "1.2.3.4.200.3");

        await Seed.InsertAsync(_db, _persistence, dataset);
        var secondWrite = await Seed.InsertAsync(_db, _persistence, dataset);

        // INSERT OR IGNORE 语义：重复 SOP 实例不应重复入库
        Assert.Equal(0, secondWrite.InsertedPatients);
        Assert.Equal(0, secondWrite.InsertedStudies);
        Assert.Equal(0, secondWrite.InsertedSeries);
        Assert.Equal(0, secondWrite.InsertedInstances);

        var repository = new DicomRepository(_db.Config);
        Assert.Single(repository.GetInstancesBySeriesUid("1.2.3.4.200.1", "1.2.3.4.200.2"));
    }

    [Fact]
    public async Task InsertBatch_MultipleStudies_SamePatient_DeduplicatesPatientRow()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var writeResult = await Seed.InsertAsync(_db, _persistence,
            DicomTestData.MakeInstance(studyUid: "1.2.3.4.300.1", seriesUid: "1.2.3.4.300.2", sopUid: "1.2.3.4.300.3"),
            DicomTestData.MakeInstance(studyUid: "1.2.3.4.300.4", seriesUid: "1.2.3.4.300.5", sopUid: "1.2.3.4.300.6"));

        // 同一患者两条研究：患者只入库一次，两条研究各自入库
        Assert.Equal(1, writeResult.InsertedPatients);
        Assert.Equal(2, writeResult.InsertedStudies);
        Assert.Equal(2, writeResult.InsertedSeries);
        Assert.Equal(2, writeResult.InsertedInstances);
    }
}
