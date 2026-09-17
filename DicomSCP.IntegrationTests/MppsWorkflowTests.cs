using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class MppsWorkflowTests
{
    private readonly E2EFixture _fx;

    public MppsWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task NCreate_ThenNSet_PersistsMpps()
    {
        var studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var mppsUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

        DicomStatus? createStatus = null;
        var mpps = _fx.CreateClient(E2EFixture.MppsPort, "MPPSSCP");
        var nCreate = new DicomNCreateRequest(
            DicomUID.ModalityPerformedProcedureStep,
            new DicomUID(mppsUid, "MPPS", DicomUidType.SOPInstance))
        {
            Dataset = TestData.CreateMpps(0, mppsUid, studyUid)
        };
        nCreate.OnResponseReceived += (_, r) => createStatus = r.Status;
        await mpps.AddRequestAsync(nCreate);
        await mpps.SendAsync();
        Assert.Equal(DicomStatus.Success, createStatus);

        var created = await _fx.WaitForAsync(async () =>
        {
            var status = await _fx.QueryScalarAsync<string>(
                "SELECT PerformedProcedureStepStatus FROM MPPS WHERE MppsId = @m", new { m = mppsUid });
            return status == "IN PROGRESS";
        }, 10000);
        Assert.True(created, $"MPPS {mppsUid} was not persisted as IN PROGRESS");

        DicomStatus? setStatus = null;
        var nSet = new DicomNSetRequest(
            DicomUID.ModalityPerformedProcedureStep,
            new DicomUID(mppsUid, "MPPS", DicomUidType.SOPInstance))
        {
            Dataset = new DicomDataset
            {
                { DicomTag.PerformedProcedureStepStatus, "COMPLETED" },
                { DicomTag.PerformedProcedureStepEndTime, "120500" }
            }
        };
        nSet.OnResponseReceived += (_, r) => setStatus = r.Status;
        await mpps.AddRequestAsync(nSet);
        await mpps.SendAsync();
        Assert.Equal(DicomStatus.Success, setStatus);

        var completed = await _fx.WaitForAsync(async () =>
        {
            var status = await _fx.QueryScalarAsync<string>(
                "SELECT PerformedProcedureStepStatus FROM MPPS WHERE MppsId = @m", new { m = mppsUid });
            return status == "COMPLETED";
        }, 10000);
        Assert.True(completed, $"MPPS {mppsUid} did not reach COMPLETED");
    }
}