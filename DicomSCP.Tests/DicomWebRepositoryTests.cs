using DicomSCP.Repository;
using FellowOakDicom;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// QIDO-RS（DICOMweb 查询）仓储方法与立即入库的测试。
/// </summary>
public class DicomWebRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DicomDatasetPersistence _persistence;
    private readonly DicomRepository _repository;

    public DicomWebRepositoryTests()
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
                patientId: "PAT001", patientName: "Zhang^San",
                studyUid: "1.2.3.4.1.1", seriesUid: "1.2.3.4.1.2", sopUid: "1.2.3.4.1.6",
                studyDate: "20240101", modality: "CT", accession: "ACC001", instanceNumber: "2"),
            DicomTestData.MakeInstance(
                patientId: "PAT002", patientName: "Li^Si",
                studyUid: "1.2.3.4.2.1", seriesUid: "1.2.3.4.2.2", sopUid: "1.2.3.4.2.3",
                studyDate: "20240301", modality: "MR", accession: "ACC002"));
    }

    private static Dictionary<DicomTag, IReadOnlyList<string>> Match(params (DicomTag Tag, string Value)[] pairs)
    {
        return pairs.ToDictionary(p => p.Tag, p => (IReadOnlyList<string>)new[] { p.Value });
    }

    [Fact]
    public async Task QidoQueryStudies_NoFilter_ReturnsAll()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(new Dictionary<DicomTag, IReadOnlyList<string>>(), fuzzy: false, offset: null, limit: null);

        Assert.Equal(2, studies.Count);
    }

    [Fact]
    public async Task QidoQueryStudies_FiltersByPatientName()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(
            Match((DicomTag.PatientName, "Li^Si")), fuzzy: false, offset: null, limit: null);

        Assert.Single(studies);
        Assert.Equal("1.2.3.4.2.1", studies[0].StudyInstanceUid);
    }

    [Fact]
    public async Task QidoQueryStudies_FuzzyMatchesPartialName()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(
            Match((DicomTag.PatientName, "Zha")), fuzzy: true, offset: null, limit: null);

        Assert.Single(studies);
        Assert.Equal("PAT001", studies[0].PatientId);
    }

    [Fact]
    public async Task QidoQueryStudies_FiltersByModalityAndComputesCounts()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(
            Match((DicomTag.ModalitiesInStudy, "CT")), fuzzy: false, offset: null, limit: null);

        Assert.Single(studies);
        Assert.Equal(1, studies[0].NumberOfStudyRelatedSeries);
        Assert.Equal(2, studies[0].NumberOfStudyRelatedInstances);
    }

    [Fact]
    public async Task QidoQueryStudies_DateRange_FiltersBetween()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(
            Match((DicomTag.StudyDate, "20240101-20240201")), fuzzy: false, offset: null, limit: null);

        Assert.Single(studies);
        Assert.Equal("20240101", studies[0].StudyDate);
    }

    [Fact]
    public async Task QidoQueryStudies_UnknownTag_IsIgnored()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(
            Match((new DicomTag(0x0009, 0x0001), "ANY")), fuzzy: false, offset: null, limit: null);

        Assert.Equal(2, studies.Count);
    }

    [Fact]
    public async Task QidoQueryStudies_Pagination_LimitsResults()
    {
        await SeedAsync();

        var studies = _repository.QidoQueryStudies(new Dictionary<DicomTag, IReadOnlyList<string>>(), fuzzy: false, offset: 0, limit: 1);

        Assert.Single(studies);
    }

    [Fact]
    public async Task QidoQuerySeries_ReturnsSeededSeriesWithinStudy()
    {
        await SeedAsync();

        var series = _repository.QidoQuerySeries("1.2.3.4.1.1", new Dictionary<DicomTag, IReadOnlyList<string>>(), fuzzy: false, offset: null, limit: null);

        Assert.Single(series);
        Assert.Equal("1.2.3.4.1.2", series[0].SeriesInstanceUid);
        Assert.Equal(2, series[0].NumberOfInstances);
    }

    [Fact]
    public async Task QidoQueryInstances_ReturnsInstancesWithinSeries()
    {
        await SeedAsync();

        var instances = _repository.QidoQueryInstances("1.2.3.4.2.1", "1.2.3.4.2.2", new Dictionary<DicomTag, IReadOnlyList<string>>(), fuzzy: false, offset: null, limit: null);

        Assert.Single(instances);
        Assert.Equal("1.2.3.4.2.3", instances[0].SopInstanceUid);
        Assert.Equal("1.2.3.4.2.1", instances[0].StudyInstanceUid);
    }

    [Fact]
    public async Task QidoQueryInstances_FiltersByInstanceNumber()
    {
        await SeedAsync();

        var instances = _repository.QidoQueryInstances("1.2.3.4.1.1", "1.2.3.4.1.2",
            Match((DicomTag.InstanceNumber, "2")), fuzzy: false, offset: null, limit: null);

        Assert.Single(instances);
        Assert.Equal("1.2.3.4.1.6", instances[0].SopInstanceUid);
    }

    [Fact]
    public async Task SaveDicomDataImmediateAsync_PersistsAndQueriesImmediately()
    {
        await DatabaseInitializer.InitializeAsync(_db.ConnectionString);

        var result = await _persistence.SaveDicomDataImmediateAsync(
            DicomTestData.MakeInstance(patientId: "PAT099"),
            "rel/1.2.3.4.9.9.dcm");

        Assert.True(result.InsertedInstances >= 1);

        var instances = _repository.QidoQueryInstances("1.2.840.113619.2.1.1.1", "1.2.840.113619.2.1.1.2",
            new Dictionary<DicomTag, IReadOnlyList<string>>(), fuzzy: false, offset: null, limit: null);

        Assert.Single(instances);
        Assert.Equal("1.2.840.113619.2.1.1.1", instances[0].StudyInstanceUid);
        Assert.Equal("1.2.840.113619.2.1.1.3", instances[0].SopInstanceUid);
    }
}