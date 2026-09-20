using DicomSCP.Models;
using FellowOakDicom;

namespace DicomSCP.Services;

/// <summary>
/// 结构化报告(SR)生成器：从 REST 输入构建 Basic Text SR 数据集。
/// 纯函数式构建，便于单元测试；归档与入库由调用方负责。
/// </summary>
public static class SrGenerator
{
    private const string DefaultConceptCodeValue = "18748-4";
    private const string DefaultConceptScheme = "DCM";
    private const string DefaultConceptMeaning = "Diagnostic imaging study";

    private const string DefaultTextConceptCodeValue = "18782-3";
    private const string DefaultTextConceptScheme = "LN";
    private const string DefaultTextConceptMeaning = "Radiology Study observation (narrative)";

    /// <summary>构建 Basic Text SR 数据集。ContentText 为空时抛 ArgumentException。</summary>
    public static DicomDataset BuildBasicTextSr(SrGenerationRequest request, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ContentText))
        {
            throw new ArgumentException("ContentText is required", nameof(request));
        }

        var studyUid = string.IsNullOrWhiteSpace(request.StudyInstanceUid)
            ? DicomUIDGenerator.GenerateDerivedFromUUID().UID
            : request.StudyInstanceUid.Trim();
        var seriesUid = string.IsNullOrWhiteSpace(request.SeriesInstanceUid)
            ? DicomUIDGenerator.GenerateDerivedFromUUID().UID
            : request.SeriesInstanceUid.Trim();
        var sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

        var studyDate = NormalizeDate(request.StudyDate, now);
        var studyTime = NormalizeTime(request.StudyTime, now);
        var completionFlag = NormalizeFlag(request.CompletionFlag, "COMPLETE", "PARTIAL", "COMPLETE");
        var verificationFlag = NormalizeFlag(request.VerificationFlag, "VERIFIED", "UNVERIFIED", "UNVERIFIED");

        var ds = new DicomDataset
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 192" },
            { DicomTag.SOPClassUID, DicomUID.BasicTextSRStorage },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.Modality, "SR" },
            { DicomTag.SeriesNumber, 1 },
            { DicomTag.InstanceNumber, 1 },
            { DicomTag.PatientID, request.PatientId ?? string.Empty },
            { DicomTag.PatientName, request.PatientName ?? string.Empty },
            { DicomTag.StudyDate, studyDate },
            { DicomTag.StudyTime, studyTime },
            { DicomTag.ContentDate, now.ToString("yyyyMMdd") },
            { DicomTag.ContentTime, now.ToString("HHmmss") },
            { DicomTag.InstanceCreationDate, now.ToString("yyyyMMdd") },
            { DicomTag.InstanceCreationTime, now.ToString("HHmmss") },
            { DicomTag.ValueType, "CONTAINER" },
            { DicomTag.ContinuityOfContent, "SEPARATE" },
            { DicomTag.CompletionFlag, completionFlag },
            { DicomTag.VerificationFlag, verificationFlag },
            { DicomTag.DocumentTitle, string.IsNullOrWhiteSpace(request.DocumentTitle) ? "Structured Report" : request.DocumentTitle.Trim() },
            { DicomTag.ConceptNameCodeSequence, MakeCodeSequence(
                request.ConceptCodeValue, DefaultConceptCodeValue,
                request.ConceptCodingSchemeDesignator, DefaultConceptScheme,
                request.ConceptCodeMeaning, DefaultConceptMeaning) },
            { DicomTag.ContentSequence, BuildContentSequence(request) }
        };

        AddIfPresent(ds, DicomTag.PatientBirthDate, request.PatientBirthDate);
        AddIfPresent(ds, DicomTag.PatientSex, request.PatientSex);
        AddIfPresent(ds, DicomTag.AccessionNumber, request.AccessionNumber);
        AddIfPresent(ds, DicomTag.StudyID, request.StudyId);
        AddIfPresent(ds, DicomTag.StudyDescription, request.StudyDescription);
        AddIfPresent(ds, DicomTag.InstitutionName, request.InstitutionName);

        if (verificationFlag == "VERIFIED")
        {
            ds.Add(DicomTag.VerificationDateTime, now.ToString("yyyyMMddHHmmss"));
        }

        if (request.ReferencedInstances is { Count: > 0 })
        {
            ds.Add(DicomTag.CurrentRequestedProcedureEvidenceSequence, BuildEvidenceSequence(request.ReferencedInstances));
        }

        return ds;
    }

    private static DicomSequence BuildContentSequence(SrGenerationRequest request)
    {
        var textItem = new DicomDataset
        {
            { DicomTag.RelationshipType, "CONTAINS" },
            { DicomTag.ValueType, "TEXT" },
            { DicomTag.ConceptNameCodeSequence, MakeCodeSequence(
                request.TextConceptCodeValue, DefaultTextConceptCodeValue,
                request.TextConceptCodingSchemeDesignator, DefaultTextConceptScheme,
                request.TextConceptCodeMeaning, DefaultTextConceptMeaning) },
            { DicomTag.TextValue, request.ContentText }
        };

        var contentSeq = new DicomSequence(DicomTag.ContentSequence);
        contentSeq.Items.Add(textItem);
        return contentSeq;
    }

    private static DicomSequence BuildEvidenceSequence(List<SrReferencedInstanceInput> references)
    {
        var studySeq = new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence);

        foreach (var studyGroup in references.GroupBy(r => r.StudyInstanceUid ?? string.Empty, StringComparer.Ordinal))
        {
            var seriesSeq = new DicomSequence(DicomTag.ReferencedSeriesSequence);
            foreach (var seriesGroup in studyGroup.GroupBy(r => r.SeriesInstanceUid ?? string.Empty, StringComparer.Ordinal))
            {
                var sopSeq = new DicomSequence(DicomTag.ReferencedSOPSequence);
                foreach (var reference in seriesGroup)
                {
                    if (string.IsNullOrWhiteSpace(reference.SopInstanceUid))
                    {
                        continue;
                    }

                    sopSeq.Items.Add(new DicomDataset
                    {
                        { DicomTag.ReferencedSOPClassUID, string.IsNullOrWhiteSpace(reference.SopClassUid) ? DicomUID.CTImageStorage.UID : reference.SopClassUid!.Trim() },
                        { DicomTag.ReferencedSOPInstanceUID, reference.SopInstanceUid.Trim() }
                    });
                }

                if (sopSeq.Items.Count == 0)
                {
                    continue;
                }

                var seriesItem = new DicomDataset { { DicomTag.SeriesInstanceUID, seriesGroup.Key } };
                seriesItem.Add(DicomTag.ReferencedSOPSequence, sopSeq);
                seriesSeq.Items.Add(seriesItem);
            }

            if (seriesSeq.Items.Count == 0)
            {
                continue;
            }

            var studyItem = new DicomDataset { { DicomTag.StudyInstanceUID, studyGroup.Key } };
            studyItem.Add(DicomTag.ReferencedSeriesSequence, seriesSeq);
            studySeq.Items.Add(studyItem);
        }

        return studySeq;
    }

    private static DicomSequence MakeCodeSequence(
        string? codeValue, string defaultCodeValue,
        string? scheme, string defaultScheme,
        string? meaning, string defaultMeaning)
    {
        var item = new DicomDataset
        {
            { DicomTag.CodeValue, string.IsNullOrWhiteSpace(codeValue) ? defaultCodeValue : codeValue.Trim() },
            { DicomTag.CodingSchemeDesignator, string.IsNullOrWhiteSpace(scheme) ? defaultScheme : scheme.Trim() },
            { DicomTag.CodeMeaning, string.IsNullOrWhiteSpace(meaning) ? defaultMeaning : meaning.Trim() }
        };

        var sequence = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        sequence.Items.Add(item);
        return sequence;
    }

    private static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ds.AddOrUpdate(tag, value.Trim());
        }
    }

    private static string NormalizeDate(string? value, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length == 8)
            {
                return digits;
            }
        }
        return now.ToString("yyyyMMdd");
    }

    private static string NormalizeTime(string? value, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 4 and <= 6)
            {
                return digits.PadRight(6, '0');
            }
        }
        return now.ToString("HHmmss");
    }

    private static string NormalizeFlag(string? value, string allowed1, string allowed2, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var normalized = value.Trim().ToUpperInvariant();
            if (normalized == allowed1 || normalized == allowed2)
            {
                return normalized;
            }
        }
        return fallback;
    }
}
