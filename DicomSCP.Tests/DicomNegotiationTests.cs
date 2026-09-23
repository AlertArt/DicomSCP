using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// PS3.8 传输语法协商与表示上下文分配测试（PC ID 奇数规则、幂等、防回绕）。
/// </summary>
public class DicomNegotiationTests
{
    [Fact]
    public void SelectBestMatch_PrefersOfferedOrder()
    {
        var offered = new[]
        {
            DicomTransferSyntax.ImplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRLittleEndian
        };
        var supported = DicomNegotiation.SupportedBasicSyntaxes;

        var best = DicomNegotiation.SelectBestMatch(offered, supported);

        // 对端偏好序优先：对端第一选择在支持集内即选它
        Assert.Equal(DicomTransferSyntax.ImplicitVRLittleEndian, best);
    }

    [Fact]
    public void SelectBestMatch_NoOverlap_ReturnsNull()
    {
        var offered = new[] { DicomTransferSyntax.DeflatedExplicitVRLittleEndian };
        var supported = DicomNegotiation.SupportedBasicSyntaxes;

        Assert.Null(DicomNegotiation.SelectBestMatch(offered, supported));
    }

    [Fact]
    public void SupportedImageSyntaxes_IncludeJpeg2000AndJpegLs()
    {
        var supported = DicomNegotiation.SupportedImageStorageSyntaxes;

        Assert.Contains(DicomTransferSyntax.JPEG2000Lossless, supported);
        Assert.Contains(DicomTransferSyntax.JPEGLSLossless, supported);
        // 兜底基础语法存在
        Assert.Contains(DicomTransferSyntax.ImplicitVRLittleEndian, supported);
    }

    [Theory]
    [InlineData("1.2.840.10008.1.2.4.90")] // JPEG 2000 Lossless
    [InlineData("1.2.840.10008.1.2.4.80")] // JPEG-LS Lossless
    public void SelectBestMatch_AcceptsJpeg2000AndJpegLs(string syntaxUid)
    {
        var offered = new[] { DicomTransferSyntax.Parse(syntaxUid) };
        var supported = DicomNegotiation.SupportedImageStorageSyntaxes;

        var best = DicomNegotiation.SelectBestMatch(offered, supported);

        Assert.NotNull(best);
        Assert.Equal(syntaxUid, best!.UID.UID);
    }

    [Fact]
    public void SelectBestMatch_FallsBackToImplicitVrLittleEndian()
    {
        // 对端只提议本端不支持的语法时，无交集 -> 由调用方回退（返回 null）
        var offered = new[]
        {
            DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
            DicomTransferSyntax.JPEGProcess14
        };
        Assert.Null(DicomNegotiation.SelectBestMatch(offered, DicomNegotiation.SupportedImageStorageSyntaxes));

        // 对端同时提供隐式 VR，小端时选取该兜底语法
        var offeredWithFallback = new[]
        {
            DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian
        };
        var best = DicomNegotiation.SelectBestMatch(offeredWithFallback, DicomNegotiation.SupportedImageStorageSyntaxes);
        Assert.Equal(DicomTransferSyntax.ImplicitVRLittleEndian, best);
    }

    [Fact]
    public void ComputeMissing_FromEmpty_AssignsOddIdsAscending()
    {
        var sopClasses = new[]
        {
            DicomUID.CTImageStorage,
            DicomUID.MRImageStorage,
            DicomUID.UltrasoundImageStorage
        };

        var result = DicomNegotiation.ComputeMissingPresentationContexts(
            Enumerable.Empty<DicomPresentationContext>(), sopClasses,
            DicomNegotiation.SupportedImageStorageSyntaxes);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].ID);   // PS3.8: PC ID 必须为奇数
        Assert.Equal(3, result[1].ID);
        Assert.Equal(5, result[2].ID);
    }

    [Fact]
    public void ComputeMissing_AlreadyCoveredAbstractSyntax_Skipped()
    {
        var sopClass = DicomUID.CTImageStorage;
        var existing = new[] { new DicomPresentationContext(1, sopClass) };

        var result = DicomNegotiation.ComputeMissingPresentationContexts(
            existing, new[] { sopClass },
            DicomNegotiation.SupportedImageStorageSyntaxes);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeMissing_SkipsUsedOddIds_AllocatesNextFree()
    {
        var existing = new[]
        {
            new DicomPresentationContext(1, DicomUID.CTImageStorage),
            new DicomPresentationContext(3, DicomUID.MRImageStorage),
            new DicomPresentationContext(5, DicomUID.UltrasoundImageStorage)
        };

        var result = DicomNegotiation.ComputeMissingPresentationContexts(
            existing, new[] { DicomUID.SecondaryCaptureImageStorage },
            DicomNegotiation.SupportedImageStorageSyntaxes);

        Assert.Single(result);
        Assert.Equal(7, result[0].ID);
    }

    [Fact]
    public void ComputeMissing_AlwaysAllocatesOddIds_EvenWhenEvenIdsUsed()
    {
        // 基线缺陷场景：既有上下文占用 2、4（偶数），新分配必须仍为奇数
        var existing = new[]
        {
            new DicomPresentationContext(2, DicomUID.CTImageStorage),
            new DicomPresentationContext(4, DicomUID.MRImageStorage)
        };

        var result = DicomNegotiation.ComputeMissingPresentationContexts(
            existing, new[] { DicomUID.SecondaryCaptureImageStorage },
            DicomNegotiation.SupportedImageStorageSyntaxes);

        Assert.Single(result);
        Assert.Equal(1, result[0].ID);
        Assert.Equal(1, result[0].ID % 2);
    }

    [Fact]
    public void ComputeMissing_IdExhaustion_StopsAtMaxWithoutOverflow()
    {
        // 128 个奇数 ID 全部分配后（1..255），再请求不再回绕分配
        var existing = Enumerable.Range(0, 128)
            .Select(i => new DicomPresentationContext((byte)(2 * i + 1), DicomUID.Generate()))
            .ToList();

        var result = DicomNegotiation.ComputeMissingPresentationContexts(
            existing,
            Enumerable.Repeat<Func<DicomUID>>(() => DicomUID.Generate(), 10).Select(f => f()),
            DicomNegotiation.SupportedImageStorageSyntaxes);

        Assert.Empty(result);
        Assert.All(existing, pc => Assert.True(pc.ID % 2 == 1));
    }

    [Fact]
    public void ComputeMissing_DuplicateSopClassesInRequest_AllocatesOnce()
    {
        var sopClass = DicomUID.CTImageStorage;

        var result = DicomNegotiation.ComputeMissingPresentationContexts(
            Enumerable.Empty<DicomPresentationContext>(),
            new[] { sopClass, sopClass, sopClass },
            DicomNegotiation.SupportedImageStorageSyntaxes);

        Assert.Single(result);
        Assert.Equal(1, result[0].ID);
    }

    [Fact]
    public void PresentationContextIdRange_MatchesPs3_8()
    {
        Assert.Equal(1, DicomNegotiation.MinPresentationContextId);
        Assert.Equal(255, DicomNegotiation.MaxPresentationContextId);
        Assert.Equal(1, DicomNegotiation.MinPresentationContextId % 2);
        Assert.Equal(1, DicomNegotiation.MaxPresentationContextId % 2);
    }
}
