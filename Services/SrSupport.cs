using FellowOakDicom;
using DicomSCP.Models;

namespace DicomSCP.Services;

/// <summary>
/// 结构化报告(SR)识别与报告级字段提取。
/// 集中维护 SR 存储 SOP 类白名单与关键属性解析，供存储协商与入库复用。
/// </summary>
public static class SrSupport
{
    /// <summary>DICOM 标准 SR 存储 SOP 类 UID 白名单。</summary>
    public static readonly IReadOnlySet<string> StorageSopClassUids = new HashSet<string>(StringComparer.Ordinal)
    {
        "1.2.840.10008.5.1.4.1.1.88.11", // Basic Text SR
        "1.2.840.10008.5.1.4.1.1.88.22", // Enhanced SR
        "1.2.840.10008.5.1.4.1.1.88.33", // Comprehensive SR
        "1.2.840.10008.5.1.4.1.1.88.34", // Comprehensive 3D SR
        "1.2.840.10008.5.1.4.1.1.88.40", // Procedure Log
        "1.2.840.10008.5.1.4.1.1.88.59", // Key Object Selection Document
    };

    public static bool IsStructuredReport(string? sopClassUid) =>
        !string.IsNullOrEmpty(sopClassUid) && StorageSopClassUids.Contains(sopClassUid);

    public static bool IsStructuredReport(DicomDataset dataset) =>
        IsStructuredReport(dataset.GetSingleValueOrDefault<string>(DicomTag.SOPClassUID, string.Empty));

    /// <summary>解析 SR 报告级字段；非 SR 或缺失时返回空字符串。</summary>
    public static SrFields Extract(DicomDataset dataset)
    {
        var (codeValue, scheme, meaning) = ExtractConceptName(dataset);

        return new SrFields(
            DocumentTitle: Safe(dataset, DicomTag.DocumentTitle),
            CompletionFlag: Safe(dataset, DicomTag.CompletionFlag),
            VerificationFlag: Safe(dataset, DicomTag.VerificationFlag),
            ConceptCodeValue: codeValue,
            ConceptCodingSchemeDesignator: scheme,
            ConceptCodeMeaning: meaning);
    }

    /// <summary>
    /// 解析 SR 证据链（引用的图像/对象）：
    /// 优先 CurrentRequestedProcedureEvidenceSequence，回退顶层 ReferencedSeriesSequence。
    /// 按被引用 SOP Instance UID 去重。
    /// </summary>
    public static IReadOnlyList<SrReference> ExtractReferences(DicomDataset dataset)
    {
        var result = new List<SrReference>();
        var srSopUid = dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(srSopUid))
        {
            return result;
        }

        try
        {
            if (dataset.Contains(DicomTag.CurrentRequestedProcedureEvidenceSequence))
            {
                foreach (var studyItem in dataset.GetSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence).Items)
                {
                    var studyUid = studyItem.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
                    if (studyItem.Contains(DicomTag.ReferencedSeriesSequence))
                    {
                        CollectSeriesReferences(studyItem.GetSequence(DicomTag.ReferencedSeriesSequence), studyUid, srSopUid, result);
                    }
                }
            }
            else if (dataset.Contains(DicomTag.ReferencedSeriesSequence))
            {
                CollectSeriesReferences(
                    dataset.GetSequence(DicomTag.ReferencedSeriesSequence),
                    dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty),
                    srSopUid,
                    result);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("SR", ex, "解析 SR 证据引用失败 - SR: {SrSop}", srSopUid);
        }

        return result
            .GroupBy(r => r.ReferencedSopInstanceUid, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static void CollectSeriesReferences(
        DicomSequence referencedSeriesSequence,
        string studyUid,
        string srSopUid,
        List<SrReference> result)
    {
        foreach (var seriesItem in referencedSeriesSequence.Items)
        {
            var seriesUid = seriesItem.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
            if (!seriesItem.Contains(DicomTag.ReferencedSOPSequence))
            {
                continue;
            }

            foreach (var sopItem in seriesItem.GetSequence(DicomTag.ReferencedSOPSequence).Items)
            {
                var referencedSopUid = sopItem.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                if (string.IsNullOrEmpty(referencedSopUid))
                {
                    continue;
                }

                result.Add(new SrReference
                {
                    SrSopInstanceUid = srSopUid,
                    ReferencedSopInstanceUid = referencedSopUid,
                    ReferencedSopClassUid = sopItem.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPClassUID, string.Empty),
                    SeriesInstanceUid = seriesUid,
                    StudyInstanceUid = studyUid
                });
            }
        }
    }

    private static string Safe(DicomDataset dataset, DicomTag tag)
    {
        try
        {
            return dataset.GetSingleValueOrDefault<string>(tag, string.Empty);
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("SR", ex, "读取 SR 属性失败 - Tag: {Tag}", tag);
            return string.Empty;
        }
    }

    /// <summary>取 ConceptNameCodeSequence 首项的 (CodeValue, CodingSchemeDesignator, CodeMeaning)。</summary>
    private static (string CodeValue, string Scheme, string Meaning) ExtractConceptName(DicomDataset dataset)
    {
        try
        {
            if (!dataset.Contains(DicomTag.ConceptNameCodeSequence))
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            var item = dataset.GetSequence(DicomTag.ConceptNameCodeSequence).Items.FirstOrDefault();
            if (item == null)
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            return (
                item.GetSingleValueOrDefault<string>(DicomTag.CodeValue, string.Empty),
                item.GetSingleValueOrDefault<string>(DicomTag.CodingSchemeDesignator, string.Empty),
                item.GetSingleValueOrDefault<string>(DicomTag.CodeMeaning, string.Empty));
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("SR", ex, "解析 ConceptNameCodeSequence 失败");
            return (string.Empty, string.Empty, string.Empty);
        }
    }
}

/// <summary>SR 报告级字段快照（落库用）。</summary>
public sealed record SrFields(
    string DocumentTitle,
    string CompletionFlag,
    string VerificationFlag,
    string ConceptCodeValue,
    string ConceptCodingSchemeDesignator,
    string ConceptCodeMeaning);
