using FellowOakDicom;

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
