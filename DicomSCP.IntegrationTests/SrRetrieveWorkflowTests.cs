using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class SrRetrieveWorkflowTests
{
    private readonly E2EFixture _fx;

    public SrRetrieveWorkflowTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task SrStoreThenFindThenQidoWado_AllSucceed()
    {
        var study = DicomUIDGenerator.GenerateDerivedFromUUID();
        var series = DicomUIDGenerator.GenerateDerivedFromUUID();
        var sop = DicomUIDGenerator.GenerateDerivedFromUUID();

        var filePath = Path.Combine(Path.GetTempPath(), "sr_" + Guid.NewGuid().ToString("N") + ".dcm");
        var ds = CreateMinimalSr(study, series, sop);
        new DicomFile(ds).Save(filePath);

        try
        {
            DicomStatus? storeStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var cstore = new DicomCStoreRequest(filePath);
            cstore.OnResponseReceived += (_, r) => storeStatus = r.Status;
            await store.AddRequestAsync(cstore);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, storeStatus);

            Assert.True(await _fx.WaitForFileAsync(sop.UID + ".dcm", 15000),
                "stored SR file not found");

            bool qrHit = false;
            await _fx.WaitForAsync(async () =>
            {
                var qr = _fx.CreateClient(E2EFixture.QrPort, "QRSCP");
                var cfind = new DicomCFindRequest(
                    DicomUID.StudyRootQueryRetrieveInformationModelFind,
                    DicomQueryRetrieveLevel.Study,
                    DicomPriority.Medium);
                cfind.Dataset = new DicomDataset
                {
                    { DicomTag.QueryRetrieveLevel, "STUDY" },
                    { DicomTag.StudyInstanceUID, study.UID }
                };
                cfind.OnResponseReceived += (_, r) =>
                {
                    if (r.Status == DicomStatus.Pending)
                    {
                        qrHit |= r.Dataset?.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, "") == study.UID;
                    }
                };
                await qr.AddRequestAsync(cfind);
                await qr.SendAsync();
                return qrHit;
            }, 20000);
            Assert.True(qrHit, "QR C-FIND did not return the stored SR study");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private static DicomDataset CreateMinimalSr(DicomUID study, DicomUID series, DicomUID sop)
    {
        var concept = new DicomDataset();
        concept.AddOrUpdate(DicomTag.CodeValue, "121311");
        concept.AddOrUpdate(DicomTag.CodingSchemeDesignator, "DCM");
        concept.AddOrUpdate(DicomTag.CodeMeaning, "Structured Report");
        var conceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        conceptSeq.Items.Add(concept);

        var root = new DicomDataset();
        root.AddOrUpdate(DicomTag.ValueType, "CONTAINER");
        root.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");
        root.Add(DicomTag.ConceptNameCodeSequence, conceptSeq);

        var contentSeq = new DicomSequence(DicomTag.ContentSequence);
        contentSeq.Items.Add(root);

        var result = new DicomDataset();
        result.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicTextSRStorage.UID);
        result.AddOrUpdate(DicomTag.SOPInstanceUID, sop.UID);
        result.AddOrUpdate(DicomTag.StudyInstanceUID, study.UID);
        result.AddOrUpdate(DicomTag.SeriesInstanceUID, series.UID);
        result.AddOrUpdate(DicomTag.SeriesNumber, 1);
        result.AddOrUpdate(DicomTag.InstanceNumber, 1);
        result.AddOrUpdate(DicomTag.Modality, "SR");
        result.AddOrUpdate(DicomTag.PatientID, "E2E-PAT-001");
        result.AddOrUpdate(DicomTag.PatientName, "E2E^Check");
        result.AddOrUpdate(DicomTag.StudyDate, "20240101");
        result.AddOrUpdate(DicomTag.StudyTime, "120000");
        result.AddOrUpdate(DicomTag.StudyID, "E2E-STUDY");
        result.AddOrUpdate(DicomTag.StudyDescription, "E2E SR Study");
        result.Add(DicomTag.ContentSequence, contentSeq);
        return result;
    }
}
