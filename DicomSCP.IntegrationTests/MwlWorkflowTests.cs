using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class MwlWorkflowTests
{
    private readonly E2EFixture _fx;

    public MwlWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task SeedViaRest_ThenScuCFindAndRestQuery_ReturnsItem()
    {
        const string patientId = "E2E-MWL-001";

        var login = await _fx.Http.PostAsJsonAsync("/api/Auth/login", new { username = "admin", password = "admin" });
        Assert.True(login.IsSuccessStatusCode, $"login failed: {(int)login.StatusCode}");

        var createPayload = new JsonObject
        {
            ["PatientId"] = patientId,
            ["PatientName"] = "E2E^MWL",
            ["Age"] = 40,
            ["AccessionNumber"] = "ACC-E2E-MWL-001",
            ["Modality"] = "CT",
            ["ScheduledDateTime"] = "2026-09-17T10:30",
            ["ScheduledAET"] = "CTSCANNER",
            ["Status"] = "SCHEDULED",
            ["StudyDescription"] = "E2E MWL"
        };
        var create = await _fx.Http.PostAsJsonAsync("/api/Worklist", createPayload);
        Assert.True(create.IsSuccessStatusCode, $"worklist create failed: {(int)create.StatusCode}");

        bool scuHit = false;
        var scu = _fx.CreateClient(E2EFixture.WorklistPort, "WORKLISTSCP");
        var cfind = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind, DicomQueryRetrieveLevel.NotApplicable, DicomPriority.Medium);
        cfind.Dataset = new DicomDataset
        {
            { DicomTag.QueryRetrieveLevel, "WORKLIST" },
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, "" },
            { DicomTag.AccessionNumber, "" },
            { DicomTag.Modality, "" },
            { DicomTag.StudyInstanceUID, "" },
            { DicomTag.ScheduledStationAETitle, "" },
            { DicomTag.ScheduledProcedureStepStartDate, "" },
            { DicomTag.ScheduledProcedureStepStartTime, "" }
        };
        cfind.OnResponseReceived += (_, r) =>
        {
            if (r.Status == DicomStatus.Pending)
            {
                scuHit |= r.Dataset?.GetSingleValueOrDefault<string>(DicomTag.PatientID, "") == patientId;
            }
        };
        await scu.AddRequestAsync(cfind);
        await scu.SendAsync();
        Assert.True(scuHit, "SCU C-FIND did not return the seeded worklist item");

        var queryResp = await _fx.Http.PostAsJsonAsync($"/api/MwlScu/query/{E2EFixture.WorklistNodeName}", new { patientId });
        Assert.True(queryResp.IsSuccessStatusCode, $"MWL REST query failed: {(int)queryResp.StatusCode}");
        var body = await queryResp.Content.ReadAsStringAsync();
        Assert.Contains(patientId, body, StringComparison.OrdinalIgnoreCase);
    }
}