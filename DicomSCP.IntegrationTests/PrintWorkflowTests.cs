using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Xunit;

namespace DicomSCP.IntegrationTests;

/// <summary>
/// Basic Grayscale Print Management SCP(B10)：Film Session/Box/Image Box 全流程
/// （N-CREATE → N-CREATE → N-SET 图像 → N-ACTION 打印）被接受，并生成可列出的打印任务。
/// </summary>
[Collection("E2E")]
public class PrintWorkflowTests
{
    private readonly E2EFixture _fx;

    public PrintWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task BasicGrayscalePrint_FullFlow_IsAcceptedAndPersisted()
    {
        var image = TestData.CreateMinimalImage();
        try
        {
            var finalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = _fx.CreateClient(E2EFixture.PrintPort, "PRINTSCP");

            // 1) N-CREATE Basic Film Session
            var sessionRequest = new DicomNCreateRequest(DicomUID.BasicFilmSession, DicomUID.Generate())
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.NumberOfCopies, "1" },
                    { DicomTag.PrintPriority, "MEDIUM" },
                    { DicomTag.MediumType, "BLUE FILM" },
                    { DicomTag.FilmDestination, "MAGAZINE" }
                }
            };
            sessionRequest.OnResponseReceived = (_, sessionResponse) =>
            {
                if (sessionResponse.Status.State != DicomState.Success) { finalTcs.TrySetResult(false); return; }
                var sessionUid = sessionResponse.Command.GetString(DicomTag.AffectedSOPInstanceUID);

                // 2) N-CREATE Basic Film Box（引用 Film Session）
                var boxRequest = new DicomNCreateRequest(DicomUID.BasicFilmBox, DicomUID.Generate())
                {
                    Dataset = new DicomDataset
                    {
                        { DicomTag.ImageDisplayFormat, "STANDARD\\1,1" },
                        { DicomTag.FilmOrientation, "PORTRAIT" },
                        { DicomTag.FilmSizeID, "8INX10IN" },
                        { DicomTag.MagnificationType, "REPLICATE" }
                    }
                };
                boxRequest.Dataset.Add(DicomTag.ReferencedFilmSessionSequence, new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomUID.BasicFilmSession },
                    { DicomTag.ReferencedSOPInstanceUID, sessionUid }
                });

                boxRequest.OnResponseReceived = (_, boxResponse) =>
                {
                    if (boxResponse.Status.State != DicomState.Success) { finalTcs.TrySetResult(false); return; }
                    var imageBox = boxResponse.Dataset?.GetSequence(DicomTag.ReferencedImageBoxSequence)?.Items.FirstOrDefault();
                    if (imageBox == null) { finalTcs.TrySetResult(false); return; }

                    var boxClassUid = imageBox.GetSingleValue<DicomUID>(DicomTag.ReferencedSOPClassUID);
                    var boxInstanceUid = imageBox.GetSingleValue<DicomUID>(DicomTag.ReferencedSOPInstanceUID);

                    // 3) N-SET Basic Grayscale Image Box（灰度页）
                    var imageRequest = new DicomNSetRequest(boxClassUid, boxInstanceUid)
                    {
                        Dataset = BuildImageBoxDataset()
                    };
                    imageRequest.OnResponseReceived = (_, imageResponse) =>
                    {
                        if (imageResponse.Status.State != DicomState.Success) { finalTcs.TrySetResult(false); return; }

                        // 4) N-ACTION 打印
                        var printRequest = new DicomNActionRequest(DicomUID.BasicFilmSession, DicomUID.Parse(sessionUid), 1);
                        printRequest.OnResponseReceived = (_, printResponse) =>
                            finalTcs.TrySetResult(printResponse.Status.State == DicomState.Success);
                        SendNext(client, printRequest, finalTcs);
                    };
                    SendNext(client, imageRequest, finalTcs);
                };
                SendNext(client, boxRequest, finalTcs);
            };

            await client.AddRequestAsync(sessionRequest);
            await client.SendAsync();

            var completed = await Task.WhenAny(finalTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(completed == finalTcs.Task && finalTcs.Task.Result,
                $"print workflow did not succeed. Server log:\n{_fx.ReadLogTail(150)}");

            // 打印任务被持久化并可列出
            Assert.True(await _fx.WaitForAsync(
                async () => await _fx.QueryCountAsync("SELECT COUNT(*) FROM PrintJobs") > 0, 10000),
                "print job was not persisted");

            using var list = await _fx.Http.GetAsync("/api/Print");
            Assert.True(list.IsSuccessStatusCode, $"print jobs listing failed: {(int)list.StatusCode}");
        }
        finally
        {
            var dir = Path.GetDirectoryName(image.FilePath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static DicomDataset BuildImageBoxDataset()
    {
        var imageDataset = new DicomDataset
        {
            { DicomTag.Columns, (ushort)8 },
            { DicomTag.Rows, (ushort)8 },
            { DicomTag.BitsAllocated, (ushort)8 },
            { DicomTag.BitsStored, (ushort)8 },
            { DicomTag.HighBit, (ushort)7 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" }
        };
        imageDataset.Add(DicomTag.PixelData, new byte[8 * 8]);

        return new DicomDataset
        {
            { DicomTag.ImageBoxPosition, (ushort)1 },
            { DicomTag.Polarity, "NORMAL" },
            { DicomTag.BasicGrayscaleImageSequence, new DicomDataset[] { imageDataset } }
        };
    }

    private static void SendNext(IDicomClient client, DicomRequest request, TaskCompletionSource<bool> tcs)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await client.AddRequestAsync(request);
                await client.SendAsync();
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
    }
}
