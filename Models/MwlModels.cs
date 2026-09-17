namespace DicomSCP.Models;

/// <summary>MWL（Modality Worklist）查询条件。</summary>
public class MwlQueryCriteria
{
    public string? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? AccessionNumber { get; set; }
    public string? Modality { get; set; }
    public string? ScheduledStationAeTitle { get; set; }
    public string? ScheduledStartDate { get; set; }
    public string? ScheduledStartTime { get; set; }
    public string? RequestedProcedureId { get; set; }
    public string? ReferringPhysician { get; set; }
}

/// <summary>MWL 返回的工作列表条目。</summary>
public class MwlWorklistItem
{
    public string? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? PatientSex { get; set; }
    public string? AccessionNumber { get; set; }
    public string? ReferringPhysician { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? StudyDate { get; set; }
    public string? StudyTime { get; set; }
    public string? StudyDescription { get; set; }
    public string? Modality { get; set; }
    public string? RequestedProcedureId { get; set; }
    public string? RequestedProcedureDescription { get; set; }
    public string? RequestingPhysician { get; set; }
    public string? ScheduledStationAeTitle { get; set; }
    public string? ScheduledProcedureStepStartDate { get; set; }
    public string? ScheduledProcedureStepStartTime { get; set; }
    public string? ScheduledStationName { get; set; }
    public string? ScheduledPerformingPhysician { get; set; }
    public string? ScheduledProcedureStepDescription { get; set; }
    public string? ScheduledProcedureStepId { get; set; }
    public string? AdmissionId { get; set; }
    public string? BodyPartExamined { get; set; }
    /// <summary>原始响应数据集（DICOM JSON），便于前端展示扩展属性。</summary>
    public string? DatasetJson { get; set; }
}