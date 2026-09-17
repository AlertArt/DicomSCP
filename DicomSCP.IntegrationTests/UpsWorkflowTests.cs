using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class UpsWorkflowTests
{
    private readonly E2EFixture _fx;

    public UpsWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task UpsNRequests_StateMachinePersistsAcrossSteps()
    {
        var upsUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var ups = _fx.CreateClient(E2EFixture.UpsPort, "UPSSCP");

        DicomStatus? createStatus = null;
        var nCreate = TestData.CreateUpsRequest(upsUid, "SCHEDULED", "UPS-E2E-001");
        nCreate.OnResponseReceived += (_, r) => createStatus = r.Status;
        await ups.AddRequestAsync(nCreate);
        await ups.SendAsync();
        Assert.Equal(DicomStatus.Success, createStatus);

        var created = await _fx.WaitForAsync(async () =>
        {
            var state = await _fx.QueryScalarAsync<string>(
                "SELECT ProcedureStepState FROM UPSWorkItems WHERE SopInstanceUid = @u", new { u = upsUid });
            return state == "SCHEDULED";
        }, 10000);
        Assert.True(created, $"UPS {upsUid} was not persisted as SCHEDULED");

        var nGet = new DicomNGetRequest(
            DicomUID.UnifiedProcedureStepPush,
            new DicomUID(upsUid, "UPS", DicomUidType.SOPInstance));
        DicomStatus? getStatus = null;
        nGet.OnResponseReceived += (_, r) => getStatus = r.Status;
        await ups.AddRequestAsync(nGet);
        await ups.SendAsync();
        Assert.Equal(DicomStatus.Success, getStatus);

        DicomStatus? changeStatus = null;
        var nChange = new DicomNActionRequest(
            DicomUID.UnifiedProcedureStepPush,
            new DicomUID(upsUid, "UPS", DicomUidType.SOPInstance),
            3)
        {
            Dataset = new DicomDataset { { new DicomTag(0x0074, 0x1000), "IN PROGRESS" } }
        };
        nChange.OnResponseReceived += (_, r) => changeStatus = r.Status;
        await ups.AddRequestAsync(nChange);
        await ups.SendAsync();
        Assert.Equal(DicomStatus.Success, changeStatus);

        var progressed = await _fx.WaitForAsync(async () =>
        {
            var state = await _fx.QueryScalarAsync<string>(
                "SELECT ProcedureStepState FROM UPSWorkItems WHERE SopInstanceUid = @u", new { u = upsUid });
            return state == "IN PROGRESS";
        }, 10000);
        Assert.True(progressed, $"UPS {upsUid} did not reach IN PROGRESS");

        DicomStatus? cancelStatus = null;
        var nCancel = new DicomNActionRequest(
            DicomUID.UnifiedProcedureStepPush,
            new DicomUID(upsUid, "UPS", DicomUidType.SOPInstance),
            2);
        nCancel.OnResponseReceived += (_, r) => cancelStatus = r.Status;
        await ups.AddRequestAsync(nCancel);
        await ups.SendAsync();
        Assert.Equal(DicomStatus.Success, cancelStatus);

        var canceled = await _fx.WaitForAsync(async () =>
        {
            var state = await _fx.QueryScalarAsync<string>(
                "SELECT ProcedureStepState FROM UPSWorkItems WHERE SopInstanceUid = @u", new { u = upsUid });
            return state == "CANCELED";
        }, 10000);
        Assert.True(canceled, $"UPS {upsUid} did not reach CANCELED");

        var ownerStatus = await _fx.QueryScalarAsync<string>(
            "SELECT ProcedureStepState FROM UPSWorkItems WHERE SopInstanceUid = @u", new { u = upsUid });
        Assert.Equal("CANCELED", ownerStatus);
    }

    [Fact]
    public async Task UpsNCreate_RejectsNonScheduledState()
    {
        var upsUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var ups = _fx.CreateClient(E2EFixture.UpsPort, "UPSSCP");

        DicomStatus? createStatus = null;
        var nCreate = TestData.CreateUpsRequest(upsUid, "IN PROGRESS", "UPS-E2E-002");
        nCreate.OnResponseReceived += (_, r) => createStatus = r.Status;
        await ups.AddRequestAsync(nCreate);
        await ups.SendAsync();
        Assert.Equal(DicomStatus.InvalidAttributeValue, createStatus);
    }
}