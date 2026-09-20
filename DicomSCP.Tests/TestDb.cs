using Microsoft.Extensions.Configuration;
using FellowOakDicom;

namespace DicomSCP.Tests;

/// <summary>
/// 测试数据库基础设施：每个测试独立的临时 SQLite 库。
/// </summary>
public sealed class TestDb : IDisposable
{
    public string DbPath { get; }
    public string ConnectionString { get; }
    public IConfiguration Config { get; }

    public TestDb()
    {
        DbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dicom_test_{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={DbPath}";
        Config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DicomDb"] = ConnectionString
            })
            .Build();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(DbPath))
            {
                File.Delete(DbPath);
            }
        }
        catch
        {
            // 临时文件清理失败不影响测试结果
        }
    }
}

/// <summary>
/// 构造测试用 DICOM 数据集（覆盖 Patient/Study/Series/Instance 四级关键标签）。
/// </summary>
public static class DicomTestData
{
    public static DicomDataset MakeInstance(
        string patientId = "PAT001",
        string patientName = "Test^Patient",
        string studyUid = "1.2.840.113619.2.1.1.1",
        string seriesUid = "1.2.840.113619.2.1.1.2",
        string sopUid = "1.2.840.113619.2.1.1.3",
        string studyDate = "20240101",
        string modality = "CT",
        string accession = "ACC001",
        string seriesNumber = "1",
        string instanceNumber = "1")
    {
        var ds = new DicomDataset();
        ds.AddOrUpdate(DicomTag.PatientID, patientId);
        ds.AddOrUpdate(DicomTag.PatientName, patientName);
        ds.AddOrUpdate(DicomTag.PatientBirthDate, "19800101");
        ds.AddOrUpdate(DicomTag.PatientSex, "M");
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.CTImageStorage);
        ds.AddOrUpdate(DicomTag.StudyDate, studyDate);
        ds.AddOrUpdate(DicomTag.Modality, modality);
        ds.AddOrUpdate(DicomTag.AccessionNumber, accession);
        ds.AddOrUpdate(DicomTag.SeriesNumber, seriesNumber);
        ds.AddOrUpdate(DicomTag.InstanceNumber, instanceNumber);
        return ds;
    }

    /// <summary>构造最小结构化报告(SR)数据集，含报告级字段与概念名。</summary>
    public static DicomDataset MakeStructuredReport(
        string patientId = "PATSR",
        string patientName = "Sr^Patient",
        string studyUid = "1.2.840.113619.2.9.1",
        string seriesUid = "1.2.840.113619.2.9.2",
        string sopUid = "1.2.840.113619.2.9.3",
        string studyDate = "20240101",
        string documentTitle = "Structured Report",
        string completionFlag = "COMPLETE",
        string verificationFlag = "UNVERIFIED",
        string conceptCodeValue = "18748-4",
        string conceptScheme = "LN",
        string conceptMeaning = "Diagnostic imaging study")
    {
        var concept = new DicomDataset();
        concept.AddOrUpdate(DicomTag.CodeValue, conceptCodeValue);
        concept.AddOrUpdate(DicomTag.CodingSchemeDesignator, conceptScheme);
        concept.AddOrUpdate(DicomTag.CodeMeaning, conceptMeaning);
        var conceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        conceptSeq.Items.Add(concept);

        var ds = new DicomDataset();
        ds.AddOrUpdate(DicomTag.PatientID, patientId);
        ds.AddOrUpdate(DicomTag.PatientName, patientName);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicTextSRStorage.UID);
        ds.AddOrUpdate(DicomTag.StudyDate, studyDate);
        ds.AddOrUpdate(DicomTag.Modality, "SR");
        ds.AddOrUpdate(DicomTag.SeriesNumber, "1");
        ds.AddOrUpdate(DicomTag.InstanceNumber, "1");
        ds.AddOrUpdate(DicomTag.DocumentTitle, documentTitle);
        ds.AddOrUpdate(DicomTag.CompletionFlag, completionFlag);
        ds.AddOrUpdate(DicomTag.VerificationFlag, verificationFlag);
        ds.Add(DicomTag.ConceptNameCodeSequence, conceptSeq);
        return ds;
    }

    /// <summary>为 SR 数据集追加证据链（CurrentRequestedProcedureEvidenceSequence）。</summary>
    public static void AddEvidence(
        DicomDataset sr,
        string referencedStudyUid,
        string referencedSeriesUid,
        string referencedSopUid,
        string referencedSopClassUid = "1.2.840.10008.5.1.4.1.1.2")
    {
        var sopItem = new DicomDataset
        {
            { DicomTag.ReferencedSOPClassUID, referencedSopClassUid },
            { DicomTag.ReferencedSOPInstanceUID, referencedSopUid }
        };
        var sopSeq = new DicomSequence(DicomTag.ReferencedSOPSequence);
        sopSeq.Items.Add(sopItem);

        var seriesItem = new DicomDataset
        {
            { DicomTag.SeriesInstanceUID, referencedSeriesUid }
        };
        seriesItem.Add(DicomTag.ReferencedSOPSequence, sopSeq);

        var seriesSeq = new DicomSequence(DicomTag.ReferencedSeriesSequence);
        seriesSeq.Items.Add(seriesItem);

        var studyItem = new DicomDataset
        {
            { DicomTag.StudyInstanceUID, referencedStudyUid }
        };
        studyItem.Add(DicomTag.ReferencedSeriesSequence, seriesSeq);

        var studySeq = new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence);
        studySeq.Items.Add(studyItem);

        sr.AddOrUpdate(DicomTag.CurrentRequestedProcedureEvidenceSequence, studySeq);
    }
}

/// <summary>
/// 将 DICOM 数据集直接批量写入测试库（绕过异步队列，保证测试确定性）。
/// </summary>
public static class Seed
{
    public static async Task<Repository.DicomDatasetPersistence.WriteResult> InsertAsync(
        TestDb db,
        Repository.DicomDatasetPersistence persistence,
        params DicomDataset[] datasets)
    {
        var items = datasets
            .Select(d => (d, $"rel/{d.GetSingleValue<string>(DicomTag.SOPInstanceUID)}.dcm"))
            .ToList();

        var batch = persistence.BuildBatchData(items, DateTime.Now);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await persistence.InsertBatchAsync(connection, transaction, batch);
        await transaction.CommitAsync();
        return result;
    }
}
