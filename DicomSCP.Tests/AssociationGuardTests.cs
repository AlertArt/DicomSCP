using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// A-ASSOCIATE-RQ 校验器单元测试（应用上下文名 + AE Title）。
/// </summary>
public class AssociationGuardTests
{
    #region 应用上下文名校验

    [Fact]
    public void ValidateApplicationContext_StandardName_Passes()
    {
        var result = AssociationGuard.ValidateApplicationContext("1.2.840.10008.3.1.1.1");

        Assert.Null(result);
    }

    [Fact]
    public void ValidateApplicationContext_NonStandardName_RejectsWithApplicationContextNotSupported()
    {
        var result = AssociationGuard.ValidateApplicationContext("1.2.826.0.1.3680043.2.1.1.1");

        Assert.Equal(DicomRejectReason.ApplicationContextNotSupported, result);
    }

    [Fact]
    public void ValidateApplicationContext_NullOrEmpty_PassesAsUnknown()
    {
        // fo-dicom 5.2.6 不暴露对端应用上下文名，null/空视为未知并放行（见 AssociationGuard 注释）
        Assert.Null(AssociationGuard.ValidateApplicationContext(null));
        Assert.Null(AssociationGuard.ValidateApplicationContext(string.Empty));
    }

    [Fact]
    public void ApplicationContextName_Constant_MatchesDicomUIDRegistry()
    {
        // 固化常量与 fo-dicom UID 字典的一致性，防止拼写漂移
        Assert.Equal(AssociationGuard.DicomApplicationContextName, DicomUID.DICOMApplicationContext.UID);
    }

    #endregion

    #region AE Title 校验

    [Fact]
    public void ValidateAETitles_MatchingCalledAE_Passes()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "STORESCP", callingAE: "CT_STATION_1",
            expectedCalledAE: "STORESCP",
            validateCallingAE: false, allowedCallingAEs: Array.Empty<string>());

        Assert.Null(result);
    }

    [Fact]
    public void ValidateAETitles_CalledAECaseInsensitive_Passes()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "StoreScp", callingAE: "CT_STATION_1",
            expectedCalledAE: "STORESCP",
            validateCallingAE: false, allowedCallingAEs: Array.Empty<string>());

        Assert.Null(result);
    }

    [Fact]
    public void ValidateAETitles_WrongCalledAE_Rejects()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "WRONGSCP", callingAE: "CT_STATION_1",
            expectedCalledAE: "STORESCP",
            validateCallingAE: false, allowedCallingAEs: Array.Empty<string>());

        Assert.Equal(DicomRejectReason.CalledAENotRecognized, result);
    }

    [Fact]
    public void ValidateAETitles_EmptyExpectedCalledAEConfig_Rejects()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "ANY", callingAE: "CT_STATION_1",
            expectedCalledAE: "",
            validateCallingAE: false, allowedCallingAEs: Array.Empty<string>());

        Assert.Equal(DicomRejectReason.CalledAENotRecognized, result);
    }

    [Fact]
    public void ValidateAETitles_EmptyCallingAE_Rejects()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "STORESCP", callingAE: "",
            expectedCalledAE: "STORESCP",
            validateCallingAE: false, allowedCallingAEs: Array.Empty<string>());

        Assert.Equal(DicomRejectReason.CallingAENotRecognized, result);
    }

    [Fact]
    public void ValidateAETitles_WhitelistOn_CallingAENotInList_Rejects()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "STORESCP", callingAE: "ROGUE_SCU",
            expectedCalledAE: "STORESCP",
            validateCallingAE: true, allowedCallingAEs: new[] { "CT_STATION_1", "MR_STATION_1" });

        Assert.Equal(DicomRejectReason.CallingAENotRecognized, result);
    }

    [Fact]
    public void ValidateAETitles_WhitelistOn_CallingAEInListCaseInsensitive_Passes()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "STORESCP", callingAE: "ct_station_1",
            expectedCalledAE: "STORESCP",
            validateCallingAE: true, allowedCallingAEs: new[] { "CT_STATION_1" });

        Assert.Null(result);
    }

    [Fact]
    public void ValidateAETitles_WhitelistOff_CallingAENotInList_Passes()
    {
        var result = AssociationGuard.ValidateAETitles(
            calledAE: "STORESCP", callingAE: "ANY_SCU",
            expectedCalledAE: "STORESCP",
            validateCallingAE: false, allowedCallingAEs: new[] { "CT_STATION_1" });

        Assert.Null(result);
    }

    #endregion

    #region 组合入口

    [Fact]
    public void Validate_NonStandardApplicationContext_TakesPrecedenceOverAETitles()
    {
        var result = AssociationGuard.Validate(
            association: null!,
            expectedCalledAE: "WRONGSCP",
            validateCallingAE: true,
            allowedCallingAEs: Array.Empty<string>(),
            applicationContextName: "1.2.826.0.1.3680043.9.999");

        // 应用上下文先于 AE 校验返回
        Assert.Equal(DicomRejectReason.ApplicationContextNotSupported, result);
    }

    #endregion
}
