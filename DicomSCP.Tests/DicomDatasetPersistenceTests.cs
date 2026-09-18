using DicomSCP.Repository;
using FellowOakDicom;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task TryReplayFailedQueue_ReinsertsDataAndDeletesRecord()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var storageRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dicom_test_store_{Guid.NewGuid():N}");
        var failedQueuePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"failed_queue_{Guid.NewGuid():N}");
        try
        {
            var relPath = "2024/01/01/1.2.3.4.900.1/1.2.3.4.900.2/1.2.3.4.900.3.dcm";
            Directory.CreateDirectory(System.IO.Path.Combine(storageRoot, "2024/01/01/1.2.3.4.900.1/1.2.3.4.900.2"));
            var ds = DicomTestData.MakeInstance(
                studyUid: "1.2.3.4.900.1", seriesUid: "1.2.3.4.900.2", sopUid: "1.2.3.4.900.3");

            await new DicomFile(ds).SaveAsync(System.IO.Path.Combine(storageRoot, relPath));

            var record = new
            {
                StudyInstanceUid = "1.2.3.4.900.1",
                SeriesInstanceUid = "1.2.3.4.900.2",
                SopInstanceUid = "1.2.3.4.900.3",
                FilePath = relPath,
                RetryCount = 5,
                FailedAt = DateTime.Now
            };
            Directory.CreateDirectory(failedQueuePath);
            var recordFile = System.IO.Path.Combine(failedQueuePath, "replay_test.json");
            await File.WriteAllTextAsync(recordFile, System.Text.Json.JsonSerializer.Serialize(record));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DicomDb"] = _db.ConnectionString,
                    ["DicomSettings:FailedQueuePath"] = failedQueuePath
                })
                .Build();
            using var replayPersistence = new DicomDatasetPersistence(config);
            await replayPersistence.TryReplayFailedQueueAsync(storageRoot);

            // 重放成功后记录文件被删除
            Assert.False(File.Exists(recordFile));

            // 数据已落入数据库
            var repository = new DicomRepository(_db.Config);
            var instances = repository.GetInstancesBySeriesUid("1.2.3.4.900.1", "1.2.3.4.900.2");
            Assert.Single(instances);
            Assert.Equal("1.2.3.4.900.3", instances[0].SopInstanceUid);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
            if (Directory.Exists(failedQueuePath)) Directory.Delete(failedQueuePath, recursive: true);
        }
    }

    [Fact]
    public async Task TryReplayFailedQueue_MissingFile_KeepsRecord()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var failedQueuePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"failed_queue_{Guid.NewGuid():N}");
        try
        {
            var record = new
            {
                StudyInstanceUid = "1.2.3.4.901.1",
                SeriesInstanceUid = "1.2.3.4.901.2",
                SopInstanceUid = "1.2.3.4.901.3",
                FilePath = "2024/01/01/1.2.3.4.901.1/1.2.3.4.901.2/missing.dcm",
                RetryCount = 5,
                FailedAt = DateTime.Now
            };
            Directory.CreateDirectory(failedQueuePath);
            var recordFile = System.IO.Path.Combine(failedQueuePath, "replay_missing.json");
            await File.WriteAllTextAsync(recordFile, System.Text.Json.JsonSerializer.Serialize(record));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DicomDb"] = _db.ConnectionString,
                    ["DicomSettings:FailedQueuePath"] = failedQueuePath
                })
                .Build();
            using var replayPersistence = new DicomDatasetPersistence(config);
            await replayPersistence.TryReplayFailedQueueAsync(System.IO.Path.GetTempPath());

            // 归档文件不存在：保留记录供下次重放
            Assert.True(File.Exists(recordFile));
        }
        finally
        {
            if (Directory.Exists(failedQueuePath)) Directory.Delete(failedQueuePath, recursive: true);
        }
    }
}
