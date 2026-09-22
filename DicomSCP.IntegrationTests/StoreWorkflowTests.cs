using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class StoreWorkflowTests
{
    private readonly E2EFixture _fx;

    public StoreWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task CStore_ThenQrFind_ThenWadoQido_AllSucceed()
    {
        var (filePath, sopUid, studyUid, seriesUid) = TestData.CreateMinimalImage();
        try
        {
            DicomStatus? storeStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(filePath);
            request.OnResponseReceived += (_, r) => storeStatus = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, storeStatus);

            Assert.True(
                await _fx.WaitForFileAsync(sopUid + ".dcm", 15000),
                $"stored file {sopUid}.dcm was not found under {_fx.Root}");

            bool qrHit = false;
            await _fx.WaitForAsync(async () =>
            {
                var qr = _fx.CreateClient(E2EFixture.QrPort, "QRSCP");
                var cfind = new DicomCFindRequest(DicomUID.StudyRootQueryRetrieveInformationModelFind, DicomQueryRetrieveLevel.Study, DicomPriority.Medium);
                cfind.Dataset = new DicomDataset
                {
                    { DicomTag.QueryRetrieveLevel, "STUDY" },
                    { DicomTag.StudyInstanceUID, studyUid }
                };
                cfind.OnResponseReceived += (_, r) =>
                {
                    if (r.Status == DicomStatus.Pending)
                    {
                        qrHit |= r.Dataset?.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, "") == studyUid;
                    }
                };
                await qr.AddRequestAsync(cfind);
                await qr.SendAsync();
                return qrHit;
            }, 20000);
            Assert.True(qrHit, $"QR C-FIND did not return the stored study. Server log:\n{_fx.ReadLogTail(150)}");

            bool qidoSeen = false;
            await _fx.WaitForAsync(async () =>
            {
                var resp = await _fx.Http.GetAsync($"/dicomweb/studies?StudyInstanceUID={studyUid}");
                if (!resp.IsSuccessStatusCode)
                {
                    return false;
                }
                var body = await resp.Content.ReadAsStringAsync();
                qidoSeen = body.Contains(studyUid, StringComparison.OrdinalIgnoreCase);
                return qidoSeen;
            }, 15000);
            Assert.True(qidoSeen, "QIDO did not return the stored study via HTTP");

            var wado = await _fx.Http.GetAsync($"/dicomweb/studies/{studyUid}/series/{seriesUid}/instances/{sopUid}");
            Assert.True(wado.IsSuccessStatusCode, $"WADO-RS failed: {(int)wado.StatusCode}");

            // WADO-RS 渲染端点：图像应返回 JPEG
            var rendered = await _fx.Http.GetAsync($"/dicomweb/studies/{studyUid}/series/{seriesUid}/instances/{sopUid}/rendered");
            Assert.True(rendered.IsSuccessStatusCode, $"WADO-RS rendered failed: {(int)rendered.StatusCode}");
            Assert.Equal("image/jpeg", rendered.Content.Headers.ContentType?.MediaType);
            Assert.True((await rendered.Content.ReadAsByteArrayAsync()).Length > 0, "rendered image body is empty");
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(filePath)))
            {
                Directory.Delete(Path.GetDirectoryName(filePath)!, recursive: true);
            }
        }
    }
}