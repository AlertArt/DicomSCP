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
}