using FellowOakDicom;

namespace DicomSCP.Services;

/// <summary>
/// 放射治疗(RT)对象识别白名单。
/// RT 计划/结构等非图像对象在 fo-dicom 运行时的 StorageCategory 归类不稳定，
/// 显式按 DICOM 标准 SOP Class UID 判断，供存储与检索协商使用。
/// </summary>
public static class RadiotherapySupport
{
    /// <summary>已支持的 RT 存储 SOP 类 UID 白名单。</summary>
    public static readonly IReadOnlySet<string> StorageSopClassUids = new HashSet<string>(StringComparer.Ordinal)
    {
        "1.2.840.10008.5.1.4.1.1.481.1", // RT Image Storage
        "1.2.840.10008.5.1.4.1.1.481.2", // RT Dose Storage
        "1.2.840.10008.5.1.4.1.1.481.3", // RT Structure Set Storage
        "1.2.840.10008.5.1.4.1.1.481.5", // RT Plan Storage
        "1.2.840.10008.5.1.4.1.1.481.8", // RT Ion Plan Storage
        "1.2.840.10008.5.1.4.1.1.481.9", // RT Ion Beams Treatment Record Storage
    };

    public static bool IsRadiotherapy(string? sopClassUid) =>
        !string.IsNullOrEmpty(sopClassUid) && StorageSopClassUids.Contains(sopClassUid);

    public static bool IsRadiotherapy(DicomUID sopClass) => IsRadiotherapy(sopClass.UID);
}
