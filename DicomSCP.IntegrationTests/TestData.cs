using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;

namespace DicomSCP.IntegrationTests;

public static class TestData
{
    public static (string FilePath, string SopUid, string StudyUid, string SeriesUid) CreateMinimalImage()
    {
        var study = DicomUIDGenerator.GenerateDerivedFromUUID();
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
            { DicomTag.PatientID, "E2E-PAT-001" },
            { DicomTag.PatientName, "E2E^Check" },
            { DicomTag.PatientBirthDate, "19900101" },
            { DicomTag.PatientSex, "M" },
            { DicomTag.StudyDate, "20240101" },
            { DicomTag.StudyTime, "120000" },
            { DicomTag.StudyID, "E2E-STUDY" },
            { DicomTag.AccessionNumber, "E2E-ACC-001" },
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

    public static DicomDataset CreateMpps(ushort statusCode, string mppsUid, string studyUid)
    {
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