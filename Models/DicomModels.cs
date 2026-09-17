namespace DicomSCP.Models;

public class Patient
{
    public string PatientId { get; set; } = null!;
    public string? PatientName { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? PatientSex { get; set; }
    public DateTime CreateTime { get; set; }
    public int NumberOfStudies { get; set; }
    public int NumberOfSeries { get; set; }
    public int NumberOfInstances { get; set; }
}

public class Study
{
    public string StudyInstanceUid { get; set; } = null!;
    public string PatientId { get; set; } = null!;
    public string PatientName { get; set; } = string.Empty;
    public string PatientSex { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string? StudyDate { get; set; }
    public string? StudyTime { get; set; }
    public string? StudyDescription { get; set; }
    public string? AccessionNumber { get; set; }
    public string? Modality { get; set; }
    public string? InstitutionName { get; set; }
    public DateTime CreateTime { get; set; }
    public int NumberOfStudyRelatedSeries { get; set; }
    public int NumberOfStudyRelatedInstances { get; set; }
}

public class Series
{
    public string SeriesInstanceUid { get; set; } = null!;
    public string StudyInstanceUid { get; set; } = null!;
    public string? Modality { get; set; }
    public string? SeriesNumber { get; set; }
    public string? SeriesDescription { get; set; }
    public string? SliceThickness { get; set; }
    public string? SeriesDate { get; set; }
    public DateTime CreateTime { get; set; }
    public int NumberOfInstances { get; set; }
    public string? StudyModality { get; set; }
}

public class Instance
{
    public string SopInstanceUid { get; set; } = null!;
    public string SeriesInstanceUid { get; set; } = null!;
    public string SopClassUid { get; set; } = null!;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string? InstanceNumber { get; set; }
    public string FilePath { get; set; } = null!;
    public int Columns { get; set; }
    public int Rows { get; set; }
    public string? PhotometricInterpretation { get; set; }
    public int BitsAllocated { get; set; }
    public int BitsStored { get; set; }
    public int PixelRepresentation { get; set; }
    public int SamplesPerPixel { get; set; }
    public string? PixelSpacing { get; set; }
    public int HighBit { get; set; }
    public string? ImageOrientationPatient { get; set; }
    public string? ImagePositionPatient { get; set; }
    public string? FrameOfReferenceUID { get; set; }
    public string? ImageType { get; set; }
    public string? WindowCenter { get; set; }
    public string? WindowWidth { get; set; }
    public DateTime CreateTime { get; set; }
}

public class WorklistItem
{
    public string WorklistId { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string PatientSex { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public string ScheduledAET { get; set; } = string.Empty;
    public string ScheduledDateTime { get; set; } = string.Empty;
    public string ScheduledStationName { get; set; } = string.Empty;
    public string ScheduledProcedureStepID { get; set; } = string.Empty;
    public string ScheduledProcedureStepDescription { get; set; } = string.Empty;
    public string RequestedProcedureID { get; set; } = string.Empty;
    public string RequestedProcedureDescription { get; set; } = string.Empty;
    public string ReferringPhysicianName { get; set; } = string.Empty;
    public string Status { get; set; } = "SCHEDULED";
    public string BodyPartExamined { get; set; } = string.Empty;
    public string ReasonForRequest { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
    public string AccessionNumber { get; set; } = string.Empty;
}

public class StudyInfo
{
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientSex { get; set; } = string.Empty;
    public string PatientBirthDate { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public int NumberOfInstances { get; set; }
}

public class SeriesInfo
{
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string SeriesNumber { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public string SeriesDescription { get; set; } = string.Empty;
    public int NumberOfInstances { get; set; }
}

public enum StorageCommitmentStatus
{
    Pending,
    Success,
    FailuresExist
}

/// <summary>存储承诺事务记录（对应表 StorageCommitments）。</summary>
public class StorageCommitmentRecord
{
    public string TransactionUid { get; set; } = string.Empty;
    public string CallingAE { get; set; } = string.Empty;
    public string Status { get; set; } = StorageCommitmentStatus.Pending.ToString();
    public int TotalCount { get; set; }
    public int FailedCount { get; set; }
    public string? FailedInstances { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? CompleteTime { get; set; }
}

/// <summary>存储承诺中引用的单个 SOP 实例。</summary>
public class ReferencedSopInstance
{
    public string SopClassUid { get; set; } = string.Empty;
    public string SopInstanceUid { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public ushort FailureReason { get; set; }
}

/// <summary>执行程序步骤（MPPS）记录，对应表 MPPS。</summary>
public class MppsRecord
{
    public string MppsId { get; set; } = string.Empty;
    public string PerformedProcedureStepId { get; set; } = string.Empty;
    public string PerformedProcedureStepStatus { get; set; } = string.Empty;
    public string? PerformedProcedureStepStartDate { get; set; }
    public string? PerformedProcedureStepStartTime { get; set; }
    public string? PerformedProcedureStepEndDate { get; set; }
    public string? PerformedProcedureStepEndTime { get; set; }
    public string? PerformedProcedureStepDescription { get; set; }
    public string? PerformedProcedureTypeDescription { get; set; }
    public string? PerformedStationAeTitle { get; set; }
    public string? PerformedStationName { get; set; }
    public string? PerformedLocation { get; set; }
    public string? PerformedProcedureStepDiscontinuationReason { get; set; }
    public string? Modality { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? AccessionNumber { get; set; }
    public string? PatientName { get; set; }
    public string? PatientId { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? PatientSex { get; set; }
    public string? CallingAE { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
    public List<MppsSeriesRecord> Series { get; set; } = new();
}

/// <summary>MPPS 中执行序列记录，对应表 MPPSSeries。</summary>
public class MppsSeriesRecord
{
    public int Id { get; set; }
    public string MppsId { get; set; } = string.Empty;
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string? Modality { get; set; }
    public string? SeriesDescription { get; set; }
    public string? ProtocolName { get; set; }
    public string? PerformingPhysicianName { get; set; }
    public string? OperatorName { get; set; }
    public string? ReferencedSopUids { get; set; }
}