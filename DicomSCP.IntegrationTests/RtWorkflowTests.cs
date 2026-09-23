using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

/// <summary>
/// 放射治疗(RT)对象导入(B11)：RTSTRUCT/RTDOSE 经 C-STORE 接收、归档、入库并可经 QIDO 检索。
/// </summary>
[Collection("E2E")]
public class RtWorkflowTests
{
    private readonly E2EFixture _fx;

    public RtWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Theory]
    [InlineData("RTSTRUCT")]
    [InlineData("RTDOSE")]
    public async Task RtObject_CStore_IsAcceptedAndQueryable(string kind)
    {
        var study = DicomUIDGenerator.GenerateDerivedFromUUID();
        var series = DicomUIDGenerator.GenerateDerivedFromUUID();
        var sop = DicomUIDGenerator.GenerateDerivedFromUUID();

        var dataset = kind == "RTSTRUCT"
            ? TestData.CreateMinimalRtStruct(study.UID, series.UID, sop.UID)
            : TestData.CreateMinimalRtDose(study.UID, series.UID, sop.UID);

        var filePath = Path.Combine(Path.GetTempPath(), "rt_" + Guid.NewGuid().ToString("N") + ".dcm");
        new DicomFile(dataset).Save(filePath);
        try
        {
            DicomStatus? storeStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(filePath);
            request.OnResponseReceived += (_, r) => storeStatus = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, storeStatus);

            Assert.True(await _fx.WaitForFileAsync(sop.UID + ".dcm", 15000), $"{kind} file not stored");
            Assert.True(await _fx.WaitForAsync(async () =>
                await _fx.QueryCountAsync(
                    "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }) > 0,
                20000), $"{kind} instance not persisted");

            // QIDO-RS 可按研究检索到 RT 对象
            using var qido = await _fx.Http.GetAsync($"/dicomweb/studies?StudyInstanceUID={study.UID}");
            Assert.True(qido.IsSuccessStatusCode, $"QIDO failed: {(int)qido.StatusCode}");
            Assert.Contains(study.UID, await qido.Content.ReadAsStringAsync());
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
