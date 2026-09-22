using FellowOakDicom;
using DicomSCP.Models;

namespace DicomSCP.Services;

/// <summary>
/// 结构化报告(SR)识别与报告级字段提取。
/// 集中维护 SR 存储 SOP 类白名单与关键属性解析，供存储协商与入库复用。
/// </summary>
public static class SrSupport
{
    /// <summary>Key Object Selection Document (KOS) SOP 类 UID。</summary>
    public const string KeyObjectSelectionSopClassUid = "1.2.840.10008.5.1.4.1.1.88.59";

    /// <summary>DICOM 标准 SR 存储 SOP 类 UID 白名单。</summary>
    public static readonly IReadOnlySet<string> StorageSopClassUids = new HashSet<string>(StringComparer.Ordinal)
    {
        "1.2.840.10008.5.1.4.1.1.88.11", // Basic Text SR
        "1.2.840.10008.5.1.4.1.1.88.22", // Enhanced SR
        "1.2.840.10008.5.1.4.1.1.88.33", // Comprehensive SR
        "1.2.840.10008.5.1.4.1.1.88.34", // Comprehensive 3D SR
        "1.2.840.10008.5.1.4.1.1.88.40", // Procedure Log
        KeyObjectSelectionSopClassUid,   // Key Object Selection Document
    };

    public static bool IsStructuredReport(string? sopClassUid) =>
        !string.IsNullOrEmpty(sopClassUid) && StorageSopClassUids.Contains(sopClassUid);

    /// <summary>是否为 Key Object Selection Document（关键对象选择，标记关键图像）。</summary>
    public static bool IsKeyObjectSelection(string? sopClassUid) =>
        string.Equals(sopClassUid, KeyObjectSelectionSopClassUid, StringComparison.Ordinal);

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
            ConceptCodeMeaning: meaning,
            VerificationDateTime: Safe(dataset, DicomTag.VerificationDateTime),
            ContentDate: Safe(dataset, DicomTag.ContentDate),
            ContentTime: Safe(dataset, DicomTag.ContentTime));
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

    /// <summary>解析 SR 文档级内容（报告头 + 内容树根）。</summary>
    public static SrDocumentContent ExtractDocumentContent(DicomDataset dataset)
    {
        return new SrDocumentContent
        {
            SopInstanceUid = dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty),
            DocumentTitle = dataset.GetSingleValueOrDefault<string>(DicomTag.DocumentTitle, string.Empty),
            CompletionFlag = dataset.GetSingleValueOrDefault<string>(DicomTag.CompletionFlag, string.Empty),
            VerificationFlag = dataset.GetSingleValueOrDefault<string>(DicomTag.VerificationFlag, string.Empty),
            ConceptName = ReadCode(dataset, DicomTag.ConceptNameCodeSequence),
            Root = ParseContentNode(dataset, isRoot: true)
        };
    }

    private static SrContentNode ParseContentNode(DicomDataset item, bool isRoot)
    {
        var node = new SrContentNode
        {
            ValueType = item.GetSingleValueOrDefault<string>(DicomTag.ValueType, string.Empty),
            RelationshipType = isRoot ? null : item.GetSingleValueOrDefault<string>(DicomTag.RelationshipType, string.Empty),
            ConceptName = ReadCode(item, DicomTag.ConceptNameCodeSequence)
        };

        switch (node.ValueType.ToUpperInvariant())
        {
            case "TEXT":
                node.TextValue = item.GetSingleValueOrDefault<string>(DicomTag.TextValue, string.Empty);
                break;
            case "CODE":
                node.Code = ReadCode(item, DicomTag.ConceptCodeSequence);
                break;
            case "NUM":
                node.NumericValue = item.GetSingleValueOrDefault<string>(DicomTag.NumericValue, string.Empty);
                node.Units = ReadCode(item, DicomTag.MeasurementUnitsCodeSequence)?.CodeMeaning;
                break;
            case "PNAME":
                node.PersonName = item.GetSingleValueOrDefault<string>(DicomTag.PersonName, string.Empty);
                break;
            case "DATETIME":
                node.DateTimeValue = item.GetSingleValueOrDefault<string>(DicomTag.DateTime, string.Empty);
                break;
            case "DATE":
                node.DateValue = item.GetSingleValueOrDefault<string>(DicomTag.Date, string.Empty);
                break;
            case "TIME":
                node.TimeValue = item.GetSingleValueOrDefault<string>(DicomTag.Time, string.Empty);
                break;
            case "UIDREF":
                node.UidValue = item.GetSingleValueOrDefault<string>(DicomTag.UID, string.Empty);
                break;
            case "IMAGE":
            case "SCOORD":
            case "SCOORD3D":
            case "WAVEFORM":
            case "COMPOSITE":
                node.ReferencedSopInstanceUids = ReadReferencedSopUids(item);
                break;
        }

        if (item.Contains(DicomTag.ContentSequence))
        {
            foreach (var child in item.GetSequence(DicomTag.ContentSequence).Items)
            {
                node.Children.Add(ParseContentNode(child, isRoot: false));
            }
        }

        return node;
    }

    private static List<string> ReadReferencedSopUids(DicomDataset item)
    {
        var uids = new List<string>();
        try
        {
            if (!item.Contains(DicomTag.ReferencedSOPSequence))
            {
                return uids;
            }

            foreach (var sopItem in item.GetSequence(DicomTag.ReferencedSOPSequence).Items)
            {
                var uid = sopItem.GetSingleValueOrDefault<string>(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                if (!string.IsNullOrEmpty(uid))
                {
                    uids.Add(uid);
                }
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("SR", ex, "解析引用型内容项失败");
        }

        return uids;
    }

    private static SrCodedConcept? ReadCode(DicomDataset dataset, DicomTag sequenceTag)
    {
        try
        {
            if (!dataset.Contains(sequenceTag))
            {
                return null;
            }

            var item = dataset.GetSequence(sequenceTag).Items.FirstOrDefault();
            if (item == null)
            {
                return null;
            }

            return new SrCodedConcept
            {
                CodeValue = item.GetSingleValueOrDefault<string>(DicomTag.CodeValue, string.Empty),
                CodingSchemeDesignator = item.GetSingleValueOrDefault<string>(DicomTag.CodingSchemeDesignator, string.Empty),
                CodeMeaning = item.GetSingleValueOrDefault<string>(DicomTag.CodeMeaning, string.Empty)
            };
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("SR", ex, "解析编码序列失败 - Tag: {Tag}", sequenceTag);
            return null;
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
    string ConceptCodeMeaning,
    string VerificationDateTime,
    string ContentDate,
    string ContentTime);
