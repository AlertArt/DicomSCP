using FellowOakDicom;
using FellowOakDicom.Network;

namespace DicomSCP.Services;

/// <summary>
/// A-ASSOCIATE-RQ 统一校验（DICOM PS3.7 / PS3.8）。
/// 返回 null 表示通过；返回拒绝原因时，调用方应以
/// DicomRejectResult.Permanent + DicomRejectSource.ServiceUser 拒绝关联。
/// </summary>
public static class AssociationGuard
{
    /// <summary>
    /// DICOM 唯一合法的应用上下文名（PS3.7 Annex A / PS3.8 7.1.1.2）。
    /// 与 DicomUID.DICOMApplicationContext.UID 对应（由单元测试固化防漂移）。
    /// </summary>
    public const string DicomApplicationContextName = "1.2.840.10008.3.1.1.1";

    /// <summary>
    /// 校验应用上下文名：非 DICOM 标准值 → ApplicationContextNotSupported。
    ///
    /// 限制说明：fo-dicom 5.2.6 的 DicomAssociation 不暴露对端 RQ 中的原始
    /// 应用上下文名（PDU 类型为 internal），因此服务端回调暂时拿不到真实值，
    /// 当前各 SCP 以 null 调用（放行，等价于基线行为）。一旦升级到暴露该字段的
    /// fo-dicom 版本，传入真实值即可立即生效，无需改动校验逻辑。
    /// </summary>
    public static DicomRejectReason? ValidateApplicationContext(string? applicationContextName)
    {
        if (string.IsNullOrEmpty(applicationContextName))
        {
            return null;
        }

        return applicationContextName == DicomApplicationContextName
            ? null
            : DicomRejectReason.ApplicationContextNotSupported;
    }

    /// <summary>
    /// 校验 Called/Calling AE Title（各 SCP 原内联校验逻辑的统一收敛，语义保持一致：
    /// Called AE 忽略大小写精确匹配；Calling AE 为空拒绝；开启白名单后按忽略
    /// 大小写匹配）。返回 null 表示通过。
    /// </summary>
    public static DicomRejectReason? ValidateAETitles(
        string? calledAE,
        string? callingAE,
        string expectedCalledAE,
        bool validateCallingAE,
        IEnumerable<string> allowedCallingAEs)
    {
        if (string.IsNullOrEmpty(expectedCalledAE) ||
            !string.Equals(expectedCalledAE, calledAE, StringComparison.OrdinalIgnoreCase))
        {
            return DicomRejectReason.CalledAENotRecognized;
        }

        if (string.IsNullOrEmpty(callingAE))
        {
            return DicomRejectReason.CallingAENotRecognized;
        }

        if (validateCallingAE &&
            !allowedCallingAEs.Contains(callingAE, StringComparer.OrdinalIgnoreCase))
        {
            return DicomRejectReason.CallingAENotRecognized;
        }

        return null;
    }

    /// <summary>
    /// A-ASSOCIATE-RQ 完整校验入口（应用上下文 + AE Title）。
    /// </summary>
    public static DicomRejectReason? Validate(
        DicomAssociation association,
        string expectedCalledAE,
        bool validateCallingAE,
        IEnumerable<string> allowedCallingAEs,
        string? applicationContextName = null)
    {
        return ValidateApplicationContext(applicationContextName)
            ?? ValidateAETitles(association.CalledAE, association.CallingAE,
                expectedCalledAE, validateCallingAE, allowedCallingAEs);
    }
}
