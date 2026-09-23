using System.Net;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class StorageCommitmentWorkflowTests
{
    private readonly E2EFixture _fx;

    public StorageCommitmentWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task NAction_VerifiesStoredInstance_AndPersistsSuccess()
    {
        var (filePath, sopUid, _, _) = TestData.CreateMinimalImage();
        try
        {
            DicomStatus? storeStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var storeReq = new DicomCStoreRequest(filePath);
            storeReq.OnResponseReceived += (_, r) => storeStatus = r.Status;
            await store.AddRequestAsync(storeReq);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, storeStatus);

            var transactionUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            var referenced = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage.UID },
                { DicomTag.ReferencedSOPInstanceUID, sopUid }
            };
            var seq = new DicomSequence(DicomTag.ReferencedSOPSequence);
            seq.Items.Add(referenced);
            var nActionDataset = new DicomDataset
            {
                { DicomTag.TransactionUID, transactionUid },
                seq
            };

            DicomStatus? actionStatus = null;
            Exception? sendError = null;
            try
            {
                var commit = _fx.CreateClient(E2EFixture.CommitmentPort, "STORECOMMITSCP");
                var nAction = new DicomNActionRequest(
                    DicomUID.StorageCommitmentPushModel,
                    DicomUID.StorageCommitmentPushModelInstance,
                    1)
                {
                    Dataset = nActionDataset
                };
                nAction.OnResponseReceived += (_, r) => actionStatus = r.Status;
                await commit.AddRequestAsync(nAction);
                await commit.SendAsync();
            }
            catch (Exception ex)
            {
                sendError = ex;
            }

            Assert.True(
                sendError == null && actionStatus != null,
                $"N-ACTION could not be delivered. Error={sendError?.Message}. Server log:\n{_fx.ReadLogTail(250)}");
            Assert.Equal(DicomStatus.Success, actionStatus);

            var persisted = await _fx.WaitForAsync(async () =>
            {
                var status = await _fx.QueryScalarAsync<string>(
                    "SELECT Status FROM StorageCommitments WHERE TransactionUid = @t", new { t = transactionUid });
                var failed = await _fx.QueryScalarAsync<int?>(
                    "SELECT FailedCount FROM StorageCommitments WHERE TransactionUid = @t", new { t = transactionUid });
                return status == "Success" && failed == 0;
            }, 20000);
            Assert.True(persisted, $"StorageCommitments row did not reach Success for {transactionUid}");
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(filePath)))
            {
                Directory.Delete(Path.GetDirectoryName(filePath)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NAction_MissingInstance_IsVisibleAsFailureAndRepushable()
    {
        var transactionUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var missingSop = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

        var referenced = new DicomDataset
        {
            { DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage.UID },
            { DicomTag.ReferencedSOPInstanceUID, missingSop }
        };
        var seq = new DicomSequence(DicomTag.ReferencedSOPSequence);
        seq.Items.Add(referenced);
        var nActionDataset = new DicomDataset
        {
            { DicomTag.TransactionUID, transactionUid },
            seq
        };

        DicomStatus? actionStatus = null;
        var commit = _fx.CreateClient(E2EFixture.CommitmentPort, "STORECOMMITSCP");
        var nAction = new DicomNActionRequest(
            DicomUID.StorageCommitmentPushModel,
            DicomUID.StorageCommitmentPushModelInstance,
            1)
        {
            Dataset = nActionDataset
        };
        nAction.OnResponseReceived += (_, r) => actionStatus = r.Status;
        await commit.AddRequestAsync(nAction);
        await commit.SendAsync();
        Assert.Equal(DicomStatus.Success, actionStatus);

        // 失败事务被持久化（绝不静默丢失）
        var persisted = await _fx.WaitForAsync(async () =>
            await _fx.QueryScalarAsync<string>(
                "SELECT Status FROM StorageCommitments WHERE TransactionUid = @t", new { t = transactionUid })
            == "FailuresExist", 20000);
        Assert.True(persisted, "failed storage commitment was not persisted");

        // 详情 API 可见失败实例
        using (var detail = await _fx.Http.GetAsync($"/api/StorageCommitment/{transactionUid}"))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var body = await detail.Content.ReadAsStringAsync();
            Assert.Contains("FailuresExist", body);
            Assert.Contains(missingSop, body);
        }

        // 列表 API 按状态过滤可见
        using (var list = await _fx.Http.GetAsync("/api/StorageCommitment?status=FailuresExist"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Contains(transactionUid, await list.Content.ReadAsStringAsync());
        }

        // 重推（目标不可达 → sent=false，接口可用并记录通知结果）
        using (var repush = await _fx.Http.PostAsync($"/api/StorageCommitment/{transactionUid}/repush", null))
        {
            Assert.Equal(HttpStatusCode.OK, repush.StatusCode);
            var body = await repush.Content.ReadAsStringAsync();
            Assert.Contains("\"sent\":false", body);
        }

        // 重推后通知尝试次数被记录
        var attempts = await _fx.QueryScalarAsync<int>(
            "SELECT NotificationAttempts FROM StorageCommitments WHERE TransactionUid = @t", new { t = transactionUid });
        Assert.True(attempts >= 1, $"notification attempts not recorded (got {attempts})");
    }
}