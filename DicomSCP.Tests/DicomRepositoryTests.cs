using DicomSCP.Repository;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// C-FIND 查询过滤逻辑测试（对应 QRSCP 使用的 DicomRepository 查询接口）。
/// </summary>
public class DicomRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DicomDatasetPersistence _persistence;
    private readonly DicomRepository _repository;

    public DicomRepositoryTests()
    {
        _persistence = new DicomDatasetPersistence(_db.Config);
        _repository = new DicomRepository(_db.Config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _db.Dispose();
    }

    private async Task SeedAsync()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);
        await Seed.InsertAsync(_db, _persistence,
            DicomTestData.MakeInstance(
                patientId: "PAT001", patientName: "Zhang^San",
                studyUid: "1.2.3.4.1.1", seriesUid: "1.2.3.4.1.2", sopUid: "1.2.3.4.1.3",
                studyDate: "20240101", modality: "CT", accession: "ACC001"),
            DicomTestData.MakeInstance(
                patientId: "PAT002", patientName: "Li^Si",
                studyUid: "1.2.3.4.2.1", seriesUid: "1.2.3.4.2.2", sopUid: "1.2.3.4.2.3",
                studyDate: "20240301", modality: "MR", accession: "ACC002"));
    }

    [Fact]
    public async Task GetStudies_NoFilter_ReturnsAllSeededStudies()
    {
        await SeedAsync();

        var studies = _repository.GetStudies("", "", "", ("", ""), null);

        Assert.Equal(2, studies.Count);
    }

    [Fact]
    public async Task GetStudies_FiltersByPatientId()
    {
        await SeedAsync();

        var studies = _repository.GetStudies("PAT001", "", "", ("", ""), null);

        Assert.Single(studies);
        Assert.Equal("PAT001", studies[0].PatientId);
        Assert.Equal("1.2.3.4.1.1", studies[0].StudyInstanceUid);
    }

    [Fact]
    public async Task GetStudies_FiltersByPatientName()
    {
        await SeedAsync();

        var studies = _repository.GetStudies("", "Li^Si", "", ("", ""), null);

        Assert.Single(studies);
        Assert.Equal("PAT002", studies[0].PatientId);
    }

    [Fact]
    public async Task GetStudies_FiltersByDateRange()
    {
        await SeedAsync();

        var studies = _repository.GetStudies("", "", "", ("20240101", "20240201"), null);

        Assert.Single(studies);
        Assert.Equal("20240101", studies[0].StudyDate);
    }

    [Fact]
    public async Task GetStudies_FiltersByModality()
    {
        await SeedAsync();

        var studies = _repository.GetStudies("", "", "", ("", ""), new[] { "MR" });

        Assert.Single(studies);
        Assert.Equal("MR", studies[0].Modality);
    }

    [Fact]
    public async Task GetStudies_FiltersByStudyInstanceUid()
    {
        await SeedAsync();

        var studies = _repository.GetStudies("", "", "", ("", ""), null, "1.2.3.4.2.1");

        Assert.Single(studies);
        Assert.Equal("1.2.3.4.2.1", studies[0].StudyInstanceUid);
    }

    [Fact]
    public async Task GetStudies_ComputesSeriesAndInstanceCounts()
    {
        await SeedAsync();
        await Seed.InsertAsync(_db, _persistence,
            DicomTestData.MakeInstance(
                studyUid: "1.2.3.4.1.1", seriesUid: "1.2.3.4.1.4", sopUid: "1.2.3.4.1.5",
                studyDate: "20240101", modality: "CT", accession: "ACC001"));

        var studies = _repository.GetStudies("PAT001", "", "", ("", ""), null);

        Assert.Single(studies);
        Assert.Equal(2, studies[0].NumberOfStudyRelatedSeries);
        Assert.Equal(2, studies[0].NumberOfStudyRelatedInstances);
    }

    [Fact]
    public async Task GetStudies_OffsetAndLimit_Paginates()
    {
        await SeedAsync();

        var page1 = _repository.GetStudies("", "", "", ("", ""), null, null, 0, 1);
        var page2 = _repository.GetStudies("", "", "", ("", ""), null, null, 1, 1);

        Assert.Single(page1);
        Assert.Single(page2);
        Assert.NotEqual(page1[0].StudyInstanceUid, page2[0].StudyInstanceUid);
    }

    [Fact]
    public async Task GetSeriesByStudyUid_ReturnsSeededSeriesWithInstanceCount()
    {
        await SeedAsync();

        var series = _repository.GetSeriesByStudyUid("1.2.3.4.1.1");

        Assert.Single(series);
        Assert.Equal("1.2.3.4.1.2", series[0].SeriesInstanceUid);
        Assert.Equal(1, series[0].NumberOfInstances);
    }

    [Fact]
    public async Task GetInstanceAsync_ReturnsSeededInstance()
    {
        await SeedAsync();

        var instance = await _repository.GetInstanceAsync("1.2.3.4.2.3");

        Assert.NotNull(instance);
        Assert.Equal("1.2.3.4.2.2", instance.SeriesInstanceUid);
        Assert.EndsWith("1.2.3.4.2.3.dcm", instance.FilePath);
    }

    [Fact]
    public async Task GetInstanceAsync_UnknownSop_ReturnsNull()
    {
        await SeedAsync();

        var instance = await _repository.GetInstanceAsync("9.9.9.9.9");

        Assert.Null(instance);
    }

    [Fact]
    public async Task GetPatients_AggregatesPatientLevelCounts()
    {
        await SeedAsync();

        var patients = _repository.GetPatients("", "");

        Assert.Equal(2, patients.Count());
        Assert.All(patients, p =>
        {
            Assert.Equal(1, p.NumberOfStudies);
            Assert.Equal(1, p.NumberOfSeries);
            Assert.Equal(1, p.NumberOfInstances);
        });
    }

    [Fact]
    public void GetStudies_ThrowOnErrorDefault_SwallowsDatabaseFailure()
    {
        var deadRepository = CreateDeadRepository();

        var result = deadRepository.GetStudies("", "", "", ("", ""), null);

        Assert.Empty(result);
    }

    [Fact]
    public void GetStudies_ThrowOnErrorTrue_PropagatesDatabaseFailure()
    {
        var deadRepository = CreateDeadRepository();

        Assert.ThrowsAny<Exception>(() =>
            deadRepository.GetStudies("", "", "", ("", ""), null, throwOnError: true));
    }

    /// <summary>构造一个指向不存在只读库的仓储，用于模拟数据库不可用。</summary>
    private static DicomRepository CreateDeadRepository()
    {
        var missingDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DicomDb"] = $"Data Source={missingDb};Mode=ReadOnly"
            })
            .Build();
        return new DicomRepository(config);
    }
}
