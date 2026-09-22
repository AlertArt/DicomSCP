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

/// <summary>关键对象选择文档(KOS)查询结果。</summary>
public class SrKeyObjectInfo
{
    public string SopInstanceUid { get; set; } = string.Empty;
    public string? StudyInstanceUid { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? DocumentTitle { get; set; }
    public string? CompletionFlag { get; set; }
    public string? VerificationFlag { get; set; }
    public string? ConceptCodeValue { get; set; }
    public string? ConceptCodeMeaning { get; set; }
}

/// <summary>SR 生成请求（REST → Basic Text SR）。</summary>
public class SrGenerationRequest
{
    public string? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? PatientBirthDate { get; set; }
    public string? PatientSex { get; set; }

    /// <summary>为空时自动生成。</summary>
    public string? StudyInstanceUid { get; set; }
    /// <summary>为空时自动生成。</summary>
    public string? SeriesInstanceUid { get; set; }

    public string? AccessionNumber { get; set; }
    public string? StudyDate { get; set; }
    public string? StudyTime { get; set; }
    public string? StudyId { get; set; }
    public string? StudyDescription { get; set; }
    public string? InstitutionName { get; set; }

    public string? DocumentTitle { get; set; }
    /// <summary>COMPLETE / PARTIAL，默认 COMPLETE。</summary>
    public string? CompletionFlag { get; set; }
    /// <summary>VERIFIED / UNVERIFIED，默认 UNVERIFIED。</summary>
    public string? VerificationFlag { get; set; }

    /// <summary>根内容项概念名（默认 DCM 18748-4 "Diagnostic imaging study"）。</summary>
    public string? ConceptCodeValue { get; set; }
    public string? ConceptCodingSchemeDesignator { get; set; }
    public string? ConceptCodeMeaning { get; set; }

    /// <summary>文本项概念名（默认 LN 18782-3 "Radiology Study observation (narrative)"）。</summary>
    public string? TextConceptCodeValue { get; set; }
    public string? TextConceptCodingSchemeDesignator { get; set; }
    public string? TextConceptCodeMeaning { get; set; }

    /// <summary>报告正文（必填）。</summary>
    public string ContentText { get; set; } = string.Empty;

    /// <summary>证据链：报告引用的图像/对象（可选）。</summary>
    public List<SrReferencedInstanceInput>? ReferencedInstances { get; set; }
}

/// <summary>SR 生成请求中引用的单个实例。</summary>
public class SrReferencedInstanceInput
{
    public string SopInstanceUid { get; set; } = string.Empty;
    public string? SopClassUid { get; set; }
    public string? SeriesInstanceUid { get; set; }
    public string? StudyInstanceUid { get; set; }
}

/// <summary>SR 生成结果。</summary>
public class SrGenerationResult
{
    public string SopInstanceUid { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>编码概念（CodeValue / CodingSchemeDesignator / CodeMeaning）。</summary>
public class SrCodedConcept
{
    public string? CodeValue { get; set; }
    public string? CodingSchemeDesignator { get; set; }
    public string? CodeMeaning { get; set; }
}

/// <summary>SR 内容树节点（对应 SR Document Content 中的一个内容项）。</summary>
public class SrContentNode
{
    public string ValueType { get; set; } = string.Empty;
    public string? RelationshipType { get; set; }
    public SrCodedConcept? ConceptName { get; set; }
    public string? TextValue { get; set; }
    /// <summary>CODE 类型的编码值。</summary>
    public SrCodedConcept? Code { get; set; }
    /// <summary>NUM 类型的测量值。</summary>
    public string? NumericValue { get; set; }
    public string? Units { get; set; }
    public string? PersonName { get; set; }
    public string? DateTimeValue { get; set; }
    public string? DateValue { get; set; }
    public string? TimeValue { get; set; }
    public string? UidValue { get; set; }
    /// <summary>IMAGE/SCOORD 等引用型内容项引用的 SOP 实例。</summary>
    public List<string>? ReferencedSopInstanceUids { get; set; }
    public List<SrContentNode> Children { get; set; } = new();
}

/// <summary>SR 文档级内容（报告头 + 内容树根）。</summary>
public class SrDocumentContent
{
    public string SopInstanceUid { get; set; } = string.Empty;
    public string? DocumentTitle { get; set; }
    public string? CompletionFlag { get; set; }
    public string? VerificationFlag { get; set; }
    public SrCodedConcept? ConceptName { get; set; }
    public SrContentNode? Root { get; set; }
}
