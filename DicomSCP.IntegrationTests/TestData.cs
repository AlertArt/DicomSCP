using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;

namespace DicomSCP.IntegrationTests;

public static class TestData
{
    public static (string FilePath, string SopUid, string StudyUid, string SeriesUid) CreateMinimalImage(
        string? patientId = null,
        string? studyUid = null,
        string? accessionNumber = null)
    {
        var study = string.IsNullOrEmpty(studyUid)
            ? DicomUIDGenerator.GenerateDerivedFromUUID()
            : new DicomUID(studyUid, "Study", DicomUidType.Unknown);
        var series = DicomUIDGenerator.GenerateDerivedFromUUID();
        var sop = DicomUIDGenerator.GenerateDerivedFromUUID();

        var ds = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, sop.UID },
            { DicomTag.StudyInstanceUID, study.UID },
            { DicomTag.SeriesInstanceUID, series.UID },
            { DicomTag.SeriesNumber, 1 },
            { DicomTag.InstanceNumber, 1 },
            { DicomTag.Modality, "CT" },
            { DicomTag.PatientID, patientId ?? "E2E-PAT-001" },
            { DicomTag.PatientName, "E2E^Check" },
            { DicomTag.PatientBirthDate, "19900101" },
            { DicomTag.PatientSex, "M" },
            { DicomTag.StudyDate, "20240101" },
            { DicomTag.StudyTime, "120000" },
            { DicomTag.StudyID, "E2E-STUDY" },
            { DicomTag.AccessionNumber, accessionNumber ?? "E2E-ACC-001" },
            { DicomTag.StudyDescription, "E2E Study" },
            { DicomTag.SeriesDescription, "E2E Series" },
            { DicomTag.Rows, (ushort)8 },
            { DicomTag.Columns, (ushort)8 },
            { DicomTag.BitsAllocated, (ushort)8 },
            { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" }
        };

        var pixels = DicomPixelData.Create(ds, true);
        pixels.AddFrame(new MemoryByteBuffer(new byte[64]));

        var dir = Path.Combine(Path.GetTempPath(), "e2eimg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, sop.UID + ".dcm");
        new DicomFile(ds).Save(filePath);
        return (filePath, sop.UID, study.UID, series.UID);
    }

    /// <summary>构造最小 Key Object Selection Document (KOS)，标记一张关键图像。</summary>
    public static DicomDataset CreateMinimalKos(
        string studyUid, string seriesUid, string sopUid,
        string refStudyUid, string refSeriesUid, string refSopUid)
    {
        var rootConceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        rootConceptSeq.Items.Add(new DicomDataset
        {
            { DicomTag.CodeValue, "113000" },
            { DicomTag.CodingSchemeDesignator, "DCM" },
            { DicomTag.CodeMeaning, "Of Interest" }
        });

        var imageConceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        imageConceptSeq.Items.Add(new DicomDataset
        {
            { DicomTag.CodeValue, "113000" },
            { DicomTag.CodingSchemeDesignator, "DCM" },
            { DicomTag.CodeMeaning, "Of Interest" }
        });

        var contentRefSopSeq = new DicomSequence(DicomTag.ReferencedSOPSequence);
        contentRefSopSeq.Items.Add(new DicomDataset
        {
            { DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage.UID },
            { DicomTag.ReferencedSOPInstanceUID, refSopUid }
        });

        var imageItem = new DicomDataset
        {
            { DicomTag.RelationshipType, "CONTAINS" },
            { DicomTag.ValueType, "IMAGE" }
        };
        imageItem.Add(DicomTag.ConceptNameCodeSequence, imageConceptSeq);
        imageItem.Add(DicomTag.ReferencedSOPSequence, contentRefSopSeq);

        var contentSeq = new DicomSequence(DicomTag.ContentSequence);
        contentSeq.Items.Add(imageItem);

        // 证据链
        var evSopSeq = new DicomSequence(DicomTag.ReferencedSOPSequence);
        evSopSeq.Items.Add(new DicomDataset
        {
            { DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage.UID },
            { DicomTag.ReferencedSOPInstanceUID, refSopUid }
        });
        var evSeriesItem = new DicomDataset { { DicomTag.SeriesInstanceUID, refSeriesUid } };
        evSeriesItem.Add(DicomTag.ReferencedSOPSequence, evSopSeq);
        var evSeriesSeq = new DicomSequence(DicomTag.ReferencedSeriesSequence);
        evSeriesSeq.Items.Add(evSeriesItem);
        var evStudyItem = new DicomDataset { { DicomTag.StudyInstanceUID, refStudyUid } };
        evStudyItem.Add(DicomTag.ReferencedSeriesSequence, evSeriesSeq);
        var evStudySeq = new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence);
        evStudySeq.Items.Add(evStudyItem);

        return new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.KeyObjectSelectionDocumentStorage },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.SeriesNumber, 1 },
            { DicomTag.InstanceNumber, 1 },
            { DicomTag.Modality, "SR" },
            { DicomTag.PatientID, "E2E-PAT-001" },
            { DicomTag.PatientName, "E2E^Check" },
            { DicomTag.StudyDate, "20240101" },
            { DicomTag.StudyTime, "120000" },
            { DicomTag.ValueType, "CONTAINER" },
            { DicomTag.ContinuityOfContent, "SEPARATE" },
            { DicomTag.CompletionFlag, "COMPLETE" },
            { DicomTag.VerificationFlag, "UNVERIFIED" },
            { DicomTag.DocumentTitle, "E2E Key Object Selection" },
            { DicomTag.ConceptNameCodeSequence, rootConceptSeq },
            { DicomTag.ContentSequence, contentSeq },
            { DicomTag.CurrentRequestedProcedureEvidenceSequence, evStudySeq }
        };
    }

    /// <summary>构造最小 RT Structure Set（无像素）。</summary>
    public static DicomDataset CreateMinimalRtStruct(string studyUid, string seriesUid, string sopUid)
    {
        return new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.RTStructureSetStorage },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.Modality, "RTSTRUCT" },
            { DicomTag.SeriesNumber, 1 },
            { DicomTag.InstanceNumber, 1 },
            { DicomTag.PatientID, "E2E-RT-001" },
            { DicomTag.PatientName, "E2E^RT" },
            { DicomTag.StudyDate, "20240101" },
            { DicomTag.StudyTime, "120000" },
            { DicomTag.StructureSetLabel, "E2E-STRUCT" },
            { DicomTag.StructureSetDate, "20240101" },
            { DicomTag.StructureSetTime, "120000" }
        };
    }

    /// <summary>构造最小 RT Dose（含像素栅格）。</summary>
    public static DicomDataset CreateMinimalRtDose(string studyUid, string seriesUid, string sopUid)
    {
        var ds = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.RTDoseStorage },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.Modality, "RTDOSE" },
            { DicomTag.SeriesNumber, 1 },
            { DicomTag.InstanceNumber, 1 },
            { DicomTag.PatientID, "E2E-RT-001" },
            { DicomTag.PatientName, "E2E^RT" },
            { DicomTag.StudyDate, "20240101" },
            { DicomTag.StudyTime, "120000" },
            { DicomTag.DoseUnits, "GY" },
            { DicomTag.DoseType, "PHYSICAL" },
            { DicomTag.Rows, (ushort)8 },
            { DicomTag.Columns, (ushort)8 },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)16 },
            { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" }
        };
        var pixels = DicomPixelData.Create(ds, true);
        pixels.AddFrame(new MemoryByteBuffer(new byte[8 * 8 * 2]));
        return ds;
    }

    public static DicomDataset CreateMpps(ushort statusCode, string mppsUid, string studyUid)    {
        var scheduled = new DicomSequence(DicomTag.ScheduledStepAttributesSequence);
        scheduled.Items.Add(new DicomDataset
        {
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.StationName, "E2E-STATION" },
            { DicomTag.Modality, "CT" },
            { DicomTag.PatientID, "E2E-PAT-001" },
            { DicomTag.PatientName, "E2E^Check" }
        });

        var ds = new DicomDataset
        {
            { DicomTag.PerformedProcedureStepStatus, statusCode == 0 ? "IN PROGRESS" : "COMPLETED" },
            { DicomTag.PerformedProcedureStepID, "PPS-E2E-001" },
            { DicomTag.PerformedProcedureStepStartDate, "20240101" },
            { DicomTag.PerformedProcedureStepStartTime, "120000" },
            { DicomTag.PerformedProcedureStepEndTime, "120500" },
            scheduled
        };
        return ds;
    }

    public static DicomNCreateRequest CreateUpsRequest(string upsUid, string state, string workitemLabel)
    {
        var ds = new DicomDataset
        {
            { new DicomTag(0x0074, 0x1000), state },
            { DicomTag.Modality, "CT" },
            { DicomTag.StationName, "E2E-STATION" },
            { DicomTag.ScheduledProcedureStepStartDateTime, "20240101120000" },
            { DicomTag.ScheduledProcedureStepPriority, "MEDIUM" },
            { DicomTag.WorklistLabel, workitemLabel },
            { DicomTag.PatientID, "E2E-PAT-001" },
            { DicomTag.PatientName, "E2E^Check" },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID }
        };
        return new DicomNCreateRequest(DicomUID.UnifiedProcedureStepPush, new DicomUID(upsUid, DicomUIDGenerator.GenerateDerivedFromUUID().UID, DicomUidType.SOPInstance))
        {
            Dataset = ds
        };
    }
}