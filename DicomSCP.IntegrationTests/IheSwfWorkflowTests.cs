using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

/// <summary>
/// IHE Scheduled Workflow (SWF/MWF) 全流程：MWL 预约 → MPPS 开始 → C-STORE 采集 → MPPS 完成 → QR 检索。
/// </summary>
[Collection("E2E")]
public class IheSwfWorkflowTests
{
    private readonly E2EFixture _fx;

    public IheSwfWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task Mwl_ThenMpps_ThenCStore_ThenQr_FullChainSucceeds()
    {
        const string patientId = "E2E-SWF-001";
        const string accession = "ACC-E2E-SWF-001";
        var studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var mppsUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

        // 1) 预约：REST 创建工作列表项
        var createPayload = new JsonObject
        {
            ["PatientId"] = patientId,
            ["PatientName"] = "E2E^SWF",
            ["Age"] = 45,
            ["AccessionNumber"] = accession,
            ["Modality"] = "CT",
            ["StudyInstanceUid"] = studyUid,
            ["ScheduledDateTime"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm"),
            ["ScheduledAET"] = "CTSCANNER",
            ["Status"] = "SCHEDULED",
            ["StudyDescription"] = "E2E SWF"
        };
        var create = await _fx.Http.PostAsJsonAsync("/api/Worklist", createPayload);
        Assert.True(create.IsSuccessStatusCode, $"worklist create failed: {(int)create.StatusCode}");

        // 2) 技师拉取工作列表：MWL C-FIND
        bool mwlHit = false;
        var mwl = _fx.CreateClient(E2EFixture.WorklistPort, "WORKLISTSCP");
        var mwlFind = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind, DicomQueryRetrieveLevel.NotApplicable, DicomPriority.Medium);
        mwlFind.Dataset = new DicomDataset
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
        mwlFind.OnResponseReceived += (_, r) =>
        {
            if (r.Status == DicomStatus.Pending)
            {
                mwlHit |= r.Dataset?.GetSingleValueOrDefault<string>(DicomTag.PatientID, "") == patientId;
            }
        };
        await mwl.AddRequestAsync(mwlFind);
        await mwl.SendAsync();
        Assert.True(mwlHit, "MWL C-FIND did not return the scheduled item");

        // 3) 开始检查：MPPS N-CREATE (IN PROGRESS)
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

        Assert.True(await _fx.WaitForAsync(async () =>
            await _fx.QueryScalarAsync<string>(
                "SELECT PerformedProcedureStepStatus FROM MPPS WHERE MppsId = @m", new { m = mppsUid })
            == "IN PROGRESS", 10000), "MPPS did not reach IN PROGRESS");

        // 4) 采集并归档：C-STORE（同一 study/patient/accession）
        var (filePath, sopUid, _, _) = TestData.CreateMinimalImage(patientId, studyUid, accession);
        try
        {
            DicomStatus? storeStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var cstore = new DicomCStoreRequest(filePath);
            cstore.OnResponseReceived += (_, r) => storeStatus = r.Status;
            await store.AddRequestAsync(cstore);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, storeStatus);

            // 5) 检查完成：MPPS N-SET (COMPLETED)
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

            Assert.True(await _fx.WaitForAsync(async () =>
                await _fx.QueryScalarAsync<string>(
                    "SELECT PerformedProcedureStepStatus FROM MPPS WHERE MppsId = @m", new { m = mppsUid })
                == "COMPLETED", 10000), "MPPS did not reach COMPLETED");

            // 6) 归档可检索：QR C-FIND（Study 级）
            bool qrHit = false;
            await _fx.WaitForAsync(async () =>
            {
                var qr = _fx.CreateClient(E2EFixture.QrPort, "QRSCP");
                var qrFind = new DicomCFindRequest(DicomUID.StudyRootQueryRetrieveInformationModelFind, DicomQueryRetrieveLevel.Study, DicomPriority.Medium);
                qrFind.Dataset = new DicomDataset
                {
                    { DicomTag.QueryRetrieveLevel, "STUDY" },
                    { DicomTag.StudyInstanceUID, studyUid }
                };
                qrFind.OnResponseReceived += (_, r) =>
                {
                    if (r.Status == DicomStatus.Pending)
                    {
                        qrHit |= r.Dataset?.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, "") == studyUid;
                    }
                };
                await qr.AddRequestAsync(qrFind);
                await qr.SendAsync();
                return qrHit;
            }, 20000);
            Assert.True(qrHit, "QR C-FIND did not return the acquired study");

            // 归档实例存在
            Assert.True(await _fx.WaitForFileAsync(sopUid + ".dcm", 15000), "stored instance file not found");
        }
        finally
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
