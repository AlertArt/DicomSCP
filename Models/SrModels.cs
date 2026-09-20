namespace DicomSCP.Models;

/// <summary>
/// SR 报告引用的一个 SOP 实例（证据链），对应表 SrReferencedInstances。
/// 来源：SR 的 CurrentRequestedProcedureEvidenceSequence（或 ReferencedSeriesSequence）。
/// </summary>
public class SrReference
{
    /// <summary>发起引用的 SR 实例 SOP Instance UID。</summary>
    public string SrSopInstanceUid { get; set; } = string.Empty;
    /// <summary>被引用的图像/对象 SOP Instance UID。</summary>
    public string ReferencedSopInstanceUid { get; set; } = string.Empty;
    public string? ReferencedSopClassUid { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? StudyInstanceUid { get; set; }
    public DateTime CreateTime { get; set; }
}

/// <summary>“报告 → 引用图像”查询结果（含本地元数据富化）。</summary>
public class SrReferenceInfo
{
    public string ReferencedSopInstanceUid { get; set; } = string.Empty;
    public string? ReferencedSopClassUid { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? Modality { get; set; }
    public string? SeriesDescription { get; set; }
    public string? InstanceNumber { get; set; }
    /// <summary>被引用实例是否已存在于本地库（可用于跳转浏览）。</summary>
    public bool PresentLocally { get; set; }
}

/// <summary>“图像 → 引用它的报告”查询结果。</summary>
public class SrReferencingInfo
{
    public string SrSopInstanceUid { get; set; } = string.Empty;
    public string? StudyInstanceUid { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? DocumentTitle { get; set; }
    public string? CompletionFlag { get; set; }
    public string? VerificationFlag { get; set; }
}
