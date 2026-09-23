using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO.Buffer;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// 图像转码覆盖：JPEG 2000 Lossless 与 JPEG-LS Lossless 可转码并可回解，
/// 覆盖 CStoreSCP 压缩落盘所依赖的编解码器。
/// </summary>
public class TranscodeTests
{
    [Theory]
    [InlineData("1.2.840.10008.1.2.4.90")] // JPEG 2000 Lossless
    [InlineData("1.2.840.10008.1.2.4.80")] // JPEG-LS Lossless
    public void Transcode_ToCompressedSyntax_RoundTrips(string targetUid)
    {
        var file = MakeImage();
        var target = DicomTransferSyntax.Parse(targetUid);

        var transcoder = new DicomTranscoder(file.Dataset.InternalTransferSyntax, target);
        var transcoded = transcoder.Transcode(file);

        Assert.Equal(target, transcoded.Dataset.InternalTransferSyntax);

        // 回解为显式 VR 小端，验证压缩数据可解码且像素长度一致
        var back = new DicomTranscoder(target, DicomTransferSyntax.ExplicitVRLittleEndian).Transcode(transcoded);
        var pixelData = DicomPixelData.Create(back.Dataset);
        Assert.Equal(64 * 64 * 2, pixelData.GetFrame(0).Data.Length); // 64 x 64 x 2 bytes
    }

    private static DicomFile MakeImage()
    {
        var ds = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.Modality, "CT" },
            { DicomTag.PatientID, "TRANSCODE-TEST" },
            { DicomTag.Rows, (ushort)64 },
            { DicomTag.Columns, (ushort)64 },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)16 },
            { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" }
        };

        var pixels = DicomPixelData.Create(ds, true);
        var bytes = new byte[64 * 64 * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }
        pixels.AddFrame(new MemoryByteBuffer(bytes));

        return new DicomFile(ds);
    }
}
