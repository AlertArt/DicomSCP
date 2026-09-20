using DicomSCP.Models;
using FellowOakDicom;
using FellowOakDicom.Serialization;

namespace DicomSCP.Services;

/// <summary>
/// DICOMweb（WADO-RS / QIDO-RS / STOW-RS）公共辅助：
/// 将数据库模型转换为 DICOM 数据集，并按 PS3.18 DICOM JSON 模型序列化。
/// </summary>
public static class DicomWebHelpers
{
    public const string JsonContentType = "application/dicom+json";
    public const string XmlContentType = "application/dicom+xml";
    public const string DicomContentType = "application/dicom";
    public const string OctetStreamContentType = "application/octet-stream";
    public const string JpegContentType = "image/jpeg";
    public const string MultipartRelated = "multipart/related";

    /// <summary>
    /// 将数据集序列化为 DICOMweb JSON（标签用十六进制形式，数字类型一律转字符串，符合 PS3.18 F.2）。
    /// 默认剔除像素数据，避免内联二进制导致响应过大。
    /// </summary>
    public static string ToDicomJson(DicomDataset? dataset, bool includePixelData = false)
    {
        if (dataset == null)
        {
            return "{}";
        }

        DicomDataset source = dataset;
        if (!includePixelData && dataset.Contains(DicomTag.PixelData))
        {
            source = dataset.Clone();
            source.Remove(DicomTag.PixelData);
        }

        return DicomJson.ConvertDicomToJson(source, false, false, NumberSerializationMode.AsString);
    }

    /// <summary>构建 Study 层 QIDO-RS 响应数据集。</summary>
    public static DicomDataset BuildStudyDataset(Study study)
    {
        var ds = new DicomDataset();
        ds.Add(DicomTag.StudyInstanceUID, study.StudyInstanceUid);
        ds.Add(DicomTag.StudyDate, study.StudyDate ?? string.Empty);
        ds.Add(DicomTag.StudyTime, study.StudyTime ?? string.Empty);
        ds.Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty);
        ds.Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty);
        ds.Add(DicomTag.ModalitiesInStudy, study.Modality ?? string.Empty);
        ds.Add(DicomTag.Modality, study.Modality ?? string.Empty);
        ds.Add(DicomTag.InstitutionName, study.InstitutionName ?? string.Empty);
        ds.Add(DicomTag.PatientID, study.PatientId ?? string.Empty);
        ds.Add(DicomTag.PatientName, study.PatientName ?? string.Empty);
        ds.Add(DicomTag.PatientSex, study.PatientSex ?? string.Empty);
        ds.Add(DicomTag.PatientBirthDate, study.PatientBirthDate ?? string.Empty);
        ds.Add(DicomTag.NumberOfStudyRelatedSeries, study.NumberOfStudyRelatedSeries);
        ds.Add(DicomTag.NumberOfStudyRelatedInstances, study.NumberOfStudyRelatedInstances);
        return ds;
    }

    /// <summary>构建 Series 层 QIDO-RS 响应数据集。</summary>
    public static DicomDataset BuildSeriesDataset(Series series)
    {
        var ds = new DicomDataset();
        ds.Add(DicomTag.StudyInstanceUID, series.StudyInstanceUid);
        ds.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
        ds.Add(DicomTag.Modality, series.Modality ?? string.Empty);
        ds.Add(DicomTag.SeriesNumber, series.SeriesNumber ?? string.Empty);
        ds.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);
        ds.Add(DicomTag.SliceThickness, series.SliceThickness ?? string.Empty);
        ds.Add(DicomTag.SeriesDate, series.SeriesDate ?? string.Empty);
        ds.Add(DicomTag.NumberOfSeriesRelatedInstances, series.NumberOfInstances);
        return ds;
    }

    /// <summary>构建 Instance 层 QIDO-RS 响应数据集。</summary>
    public static DicomDataset BuildInstanceDataset(Instance instance)
    {
        var ds = new DicomDataset();
        ds.Add(DicomTag.StudyInstanceUID, instance.StudyInstanceUid ?? string.Empty);
        ds.Add(DicomTag.SeriesInstanceUID, instance.SeriesInstanceUid);
        ds.Add(DicomTag.SOPInstanceUID, instance.SopInstanceUid);
        ds.Add(DicomTag.SOPClassUID, instance.SopClassUid);
        ds.Add(DicomTag.InstanceNumber, instance.InstanceNumber ?? string.Empty);
        // US（无符号短整型）属性必须用 ushort 构造，否则 fo-dicom 拒绝 int 值
        ds.Add(DicomTag.Rows, ToUS(instance.Rows));
        ds.Add(DicomTag.Columns, ToUS(instance.Columns));
        ds.Add(DicomTag.PhotometricInterpretation, instance.PhotometricInterpretation ?? string.Empty);
        ds.Add(DicomTag.BitsAllocated, ToUS(instance.BitsAllocated));
        ds.Add(DicomTag.BitsStored, ToUS(instance.BitsStored));
        ds.Add(DicomTag.PixelRepresentation, ToUS(instance.PixelRepresentation));
        ds.Add(DicomTag.SamplesPerPixel, ToUS(instance.SamplesPerPixel));
        if (!string.IsNullOrEmpty(instance.PixelSpacing))
            ds.Add(DicomTag.PixelSpacing, instance.PixelSpacing);
        ds.Add(DicomTag.HighBit, ToUS(instance.HighBit));
        if (!string.IsNullOrEmpty(instance.ImageOrientationPatient))
            ds.Add(DicomTag.ImageOrientationPatient, instance.ImageOrientationPatient);
        if (!string.IsNullOrEmpty(instance.ImagePositionPatient))
            ds.Add(DicomTag.ImagePositionPatient, instance.ImagePositionPatient);
        if (!string.IsNullOrEmpty(instance.FrameOfReferenceUID))
            ds.Add(DicomTag.FrameOfReferenceUID, instance.FrameOfReferenceUID);
        if (!string.IsNullOrEmpty(instance.ImageType))
            ds.Add(DicomTag.ImageType, instance.ImageType);
        if (!string.IsNullOrEmpty(instance.WindowCenter))
            ds.Add(DicomTag.WindowCenter, instance.WindowCenter);
        if (!string.IsNullOrEmpty(instance.WindowWidth))
            ds.Add(DicomTag.WindowWidth, instance.WindowWidth);
        // SR 报告级字段
        if (!string.IsNullOrEmpty(instance.DocumentTitle))
            ds.Add(DicomTag.DocumentTitle, instance.DocumentTitle);
        if (!string.IsNullOrEmpty(instance.CompletionFlag))
            ds.Add(DicomTag.CompletionFlag, instance.CompletionFlag);
        if (!string.IsNullOrEmpty(instance.VerificationFlag))
            ds.Add(DicomTag.VerificationFlag, instance.VerificationFlag);
        if (!string.IsNullOrEmpty(instance.ConceptCodeValue) ||
            !string.IsNullOrEmpty(instance.ConceptCodeMeaning))
        {
            var concept = new DicomDataset();
            if (!string.IsNullOrEmpty(instance.ConceptCodeValue))
                concept.Add(DicomTag.CodeValue, instance.ConceptCodeValue);
            if (!string.IsNullOrEmpty(instance.ConceptCodingSchemeDesignator))
                concept.Add(DicomTag.CodingSchemeDesignator, instance.ConceptCodingSchemeDesignator);
            if (!string.IsNullOrEmpty(instance.ConceptCodeMeaning))
                concept.Add(DicomTag.CodeMeaning, instance.ConceptCodeMeaning);
            var conceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
            conceptSeq.Items.Add(concept);
            ds.Add(DicomTag.ConceptNameCodeSequence, conceptSeq);
        }
        return ds;
    }

    /// <summary>将数据库中的整数字段安全转换为 US（无符号短整型）值。</summary>
    private static ushort ToUS(int value) => value < 0 ? (ushort)0 : (ushort)Math.Min(value, ushort.MaxValue);
}