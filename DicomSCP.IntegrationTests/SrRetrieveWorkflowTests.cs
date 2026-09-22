using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
        var (study, series, sop) = NewUids();

        var filePath = await StoreSrAsync(study, series, sop);
        try
        {
            // DIMSE C-FIND 必须先能检索到该 SR 研究（等待批量入库落库）
            Assert.True(await WaitForQrHitAsync(study.UID), "QR C-FIND did not return the stored SR study");

            // QIDO-RS：研究级查询应命中
            using (var qidoStudy = await GetAsync($"/dicomweb/studies?StudyInstanceUID={study.UID}", "application/dicom+json"))
            {
                Assert.Equal(HttpStatusCode.OK, qidoStudy.StatusCode);
                Assert.Contains(study.UID, await qidoStudy.Content.ReadAsStringAsync());
            }

            // QIDO-RS：实例级查询应命中
            using (var qidoInst = await GetAsync($"/dicomweb/studies/{study.UID}/series/{series.UID}/instances", "application/dicom+json"))
            {
                Assert.Equal(HttpStatusCode.OK, qidoInst.StatusCode);
                Assert.Contains(sop.UID, await qidoInst.Content.ReadAsStringAsync());
            }

            // WADO-RS：实例元数据（DICOM JSON）应可检索
            using (var metadata = await GetAsync($"/dicomweb/studies/{study.UID}/series/{series.UID}/instances/{sop.UID}/metadata", "application/dicom+json"))
            {
                Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
                var body = await metadata.Content.ReadAsStringAsync();
                Assert.Contains(sop.UID, body);
                Assert.Contains("1.2.840.10008.5.1.4.1.1.88.11", body);
            }

            // WADO-RS：实例本体（DICOM）应可检索
            using (var retrieve = await GetAsync($"/dicomweb/studies/{study.UID}/series/{series.UID}/instances/{sop.UID}", "multipart/related; type=\"application/dicom\""))
            {
                Assert.Equal(HttpStatusCode.OK, retrieve.StatusCode);
                Assert.Equal("application/dicom", retrieve.Content.Headers.ContentType?.MediaType);
                Assert.True((await retrieve.Content.ReadAsByteArrayAsync()).Length > 0, "retrieved SR body is empty");
            }
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    [Fact]
    public async Task SrReportFieldsPersistedAndQueryable()
    {
        var (study, series, sop) = NewUids();
        const string title = "E2E Structured Report";

        var filePath = await StoreSrAsync(study, series, sop);
        try
        {
            // 报告级字段已落库
            Assert.Equal(title, await _fx.QueryScalarAsync<string>(
                "SELECT DocumentTitle FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }));
            Assert.Equal("COMPLETE", await _fx.QueryScalarAsync<string>(
                "SELECT CompletionFlag FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }));
            Assert.Equal("UNVERIFIED", await _fx.QueryScalarAsync<string>(
                "SELECT VerificationFlag FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }));
            Assert.Equal("20240101120000", await _fx.QueryScalarAsync<string>(
                "SELECT VerificationDateTime FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }));
            Assert.Equal("18748-4", await _fx.QueryScalarAsync<string>(
                "SELECT ConceptCodeValue FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }));

            var encodedTitle = Uri.EscapeDataString(title);

            // QIDO-RS 实例级按 DocumentTitle 过滤命中，且响应暴露报告字段
            using (var qidoInst = await GetAsync(
                $"/dicomweb/studies/{study.UID}/series/{series.UID}/instances?DocumentTitle={encodedTitle}",
                "application/dicom+json"))
            {
                Assert.Equal(HttpStatusCode.OK, qidoInst.StatusCode);
                var body = await qidoInst.Content.ReadAsStringAsync();
                Assert.Contains(sop.UID, body);
                // DICOM JSON 使用十六进制标签：00420010 = DocumentTitle
                Assert.Contains("00420010", body);
                Assert.Contains(title, body);
            }

            // QIDO-RS 研究级按 DocumentTitle 过滤命中
            using (var qidoStudy = await GetAsync(
                $"/dicomweb/studies?DocumentTitle={encodedTitle}", "application/dicom+json"))
            {
                Assert.Equal(HttpStatusCode.OK, qidoStudy.StatusCode);
                Assert.Contains(study.UID, await qidoStudy.Content.ReadAsStringAsync());
            }

            // QIDO-RS 实例级按概念码 CodeValue 过滤命中（00080100 = CodeValue）
            using (var qidoCode = await GetAsync(
                $"/dicomweb/studies/{study.UID}/series/{series.UID}/instances?CodeValue=18748-4",
                "application/dicom+json"))
            {
                Assert.Equal(HttpStatusCode.OK, qidoCode.StatusCode);
                var body = await qidoCode.Content.ReadAsStringAsync();
                Assert.Contains(sop.UID, body);
                Assert.Contains("00080100", body);
            }

            // DIMSE Image 级 C-FIND 按 DocumentTitle 过滤命中
            Assert.True(await ImageLevelFindHitsAsync(study, series, sop, title),
                "image-level C-FIND by DocumentTitle did not return the SR instance");
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    [Fact]
    public async Task SrNonImageRetrieval_FallsBackToDicomOrReturns415()
    {
        var (study, series, sop) = NewUids();

        var filePath = await StoreSrAsync(study, series, sop);
        try
        {
            // WADO-RS 帧检索对无像素的 SR 应返回 415，而不是 500
            using (var frames = await GetAsync($"/dicomweb/studies/{study.UID}/series/{series.UID}/instances/{sop.UID}/frames/1"))
            {
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, frames.StatusCode);
            }

            // WADO-RS 渲染端点对无像素的 SR 应返回 415
            using (var rendered = await GetAsync($"/dicomweb/studies/{study.UID}/series/{series.UID}/instances/{sop.UID}/rendered"))
            {
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, rendered.StatusCode);
            }

            // 传统 WADO 显式请求 JPEG 渲染对 SR 应返回 415，而不是 500
            using (var wadoJpeg = await GetAsync(
                $"/wado?requestType=WADO&studyUID={study.UID}&seriesUID={series.UID}&objectUID={sop.UID}&contentType=image/jpeg"))
            {
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, wadoJpeg.StatusCode);
            }

            // 传统 WADO 未指定内容类型时，SR 应回退为返回 DICOM 本体
            using (var wadoDefault = await GetAsync(
                $"/wado?requestType=WADO&studyUID={study.UID}&seriesUID={series.UID}&objectUID={sop.UID}"))
            {
                Assert.Equal(HttpStatusCode.OK, wadoDefault.StatusCode);
                Assert.Equal("application/dicom", wadoDefault.Content.Headers.ContentType?.MediaType);
            }
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    [Fact]
    public async Task SrEvidenceLinksReportToReferencedImage()
    {
        // 先存入一张 CT 图像，作为 SR 的证据
        var (imagePath, imageSop, imageStudy, imageSeries) = TestData.CreateMinimalImage();
        try
        {
            DicomStatus? imageStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var imageRequest = new DicomCStoreRequest(imagePath);
            imageRequest.OnResponseReceived += (_, r) => imageStatus = r.Status;
            await store.AddRequestAsync(imageRequest);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, imageStatus);

            // 等待图像异步批量入库，保证关联查询能富化到本地实例
            Assert.True(await _fx.WaitForAsync(
                async () => await _fx.QueryCountAsync(
                    "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = imageSop }) > 0,
                20000), "referenced image was not persisted to the database");
        }
        finally
        {
            var dir = Path.GetDirectoryName(imagePath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        var (study, series, sop) = NewUids();
        var srPath = await StoreSrAsync(study, series, sop, (imageStudy, imageSeries, imageSop));
        try
        {
            // 报告 → 引用图像
            using (var references = await GetAsync($"/api/Sr/{sop.UID}/references", "application/json"))
            {
                Assert.Equal(HttpStatusCode.OK, references.StatusCode);
                var body = await references.Content.ReadAsStringAsync();
                Assert.Contains(imageSop, body);
                Assert.Contains("\"presentLocally\":true", body);
            }

            // 图像 → 引用它的报告
            using (var referencing = await GetAsync($"/api/Sr/referencing/{imageSop}", "application/json"))
            {
                Assert.Equal(HttpStatusCode.OK, referencing.StatusCode);
                var body = await referencing.Content.ReadAsStringAsync();
                Assert.Contains(sop.UID, body);
                Assert.Contains("E2E Structured Report", body);
            }
        }
        finally
        {
            DeleteTemp(srPath);
        }
    }

    [Fact]
    public async Task SrGenerate_ArchivesPersistsAndLinksEvidence()
    {
        var (imageSop, imageStudy, imageSeries) = await StoreMinimalImageAsync();

        var payload = new
        {
            patientId = "E2E-PAT-001",
            patientName = "E2E^Check",
            documentTitle = "E2E Generated Report",
            contentText = "Generated by integration test.",
            verificationFlag = "VERIFIED",
            referencedInstances = new[]
            {
                new
                {
                    studyInstanceUid = imageStudy,
                    seriesInstanceUid = imageSeries,
                    sopInstanceUid = imageSop,
                    sopClassUid = "1.2.840.10008.5.1.4.1.1.2"
                }
            }
        };

        using var response = await _fx.Http.PostAsJsonAsync("/api/Sr/generate", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var srSop = root.GetProperty("sopInstanceUid").GetString()!;
        var srStudy = root.GetProperty("studyInstanceUid").GetString()!;
        Assert.False(string.IsNullOrEmpty(srSop));

        // 归档文件存在
        Assert.True(await _fx.WaitForFileAsync(srSop + ".dcm", 15000), "generated SR file not found");

        // 已入库且报告字段正确
        Assert.Equal("E2E Generated Report", await _fx.QueryScalarAsync<string>(
            "SELECT DocumentTitle FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = srSop }));

        // 生成的 SR 可被 QIDO-RS 检索
        using (var qido = await GetAsync($"/dicomweb/studies?StudyInstanceUID={srStudy}", "application/dicom+json"))
        {
            Assert.Equal(HttpStatusCode.OK, qido.StatusCode);
            Assert.Contains(srStudy, await qido.Content.ReadAsStringAsync());
        }

        // 证据链关联到被引用图像
        using (var references = await GetAsync($"/api/Sr/{srSop}/references", "application/json"))
        {
            Assert.Equal(HttpStatusCode.OK, references.StatusCode);
            Assert.Contains(imageSop, await references.Content.ReadAsStringAsync());
        }

        // 内容树 API 返回报告头与文本内容
        using (var content = await GetAsync($"/api/Sr/{srSop}/content", "application/json"))
        {
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            var body = await content.Content.ReadAsStringAsync();
            Assert.Contains("E2E Generated Report", body);
            Assert.Contains("Generated by integration test.", body);
            Assert.Contains("CONTAINER", body);
        }
    }

    [Fact]
    public async Task SrMalformedReport_IsRejectedWithInvalidAttributeValue()
    {
        var (study, series, sop) = NewUids();
        var filePath = Path.Combine(Path.GetTempPath(), "srbad_" + Guid.NewGuid().ToString("N") + ".dcm");

        // 缺少 SR 必需的 CompletionFlag，属畸形报告
        var ds = CreateMinimalSr(study, series, sop);
        ds.Remove(DicomTag.CompletionFlag);
        new DicomFile(ds).Save(filePath);

        try
        {
            DicomStatus? status = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(filePath);
            request.OnResponseReceived += (_, r) => status = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();

            Assert.Equal(DicomStatus.InvalidAttributeValue, status);
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    [Fact]
    public async Task SrStowRs_StoresAndRetrievesReport()
    {
        var (study, series, sop) = NewUids();
        var ds = CreateMinimalSr(study, series, sop);

        using var fileStream = new MemoryStream();
        await new DicomFile(ds).SaveAsync(fileStream);
        var bytes = fileStream.ToArray();

        var boundary = "dicomweb-" + Guid.NewGuid().ToString("N");
        using var multipart = new MultipartContent("related", boundary);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");
        multipart.Add(part);

        using var response = await _fx.Http.PostAsync("/dicomweb/studies", multipart);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(await _fx.WaitForFileAsync(sop.UID + ".dcm", 15000), "STOW-RS stored SR file not found");

        Assert.Equal("E2E Structured Report", await _fx.QueryScalarAsync<string>(
            "SELECT DocumentTitle FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }));

        using var qido = await GetAsync($"/dicomweb/studies?StudyInstanceUID={study.UID}", "application/dicom+json");
        Assert.Equal(HttpStatusCode.OK, qido.StatusCode);
        Assert.Contains(study.UID, await qido.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SrCGet_RetrievesReport()
    {
        var (study, series, sop) = NewUids();
        var filePath = await StoreSrAsync(study, series, sop);
        try
        {
            var client = _fx.CreateClient(E2EFixture.QrPort, "QRSCP");
            // C-GET 需要客户端以 SCP 角色接受存储 SOP 类（否则服务端无法回推 C-STORE）
            var srContext = DicomPresentationContext.GetScpRolePresentationContextsFromStorageUids(
                    null, DicomTransferSyntax.ImplicitVRLittleEndian)
                .First(pc => pc.AbstractSyntax.UID == DicomUID.BasicTextSRStorage.UID);
            client.AdditionalPresentationContexts.Add(srContext);

            var received = new List<string>();
            client.OnCStoreRequest = request =>
            {
                received.Add(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, string.Empty));
                return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
            };

            DicomStatus? getStatus = null;
            var get = new DicomCGetRequest(study.UID, DicomPriority.Medium);
            get.OnResponseReceived += (_, r) =>
            {
                if (r.Status != DicomStatus.Pending)
                {
                    getStatus = r.Status;
                }
            };
            await client.AddRequestAsync(get);
            await client.SendAsync();

            Assert.Equal(DicomStatus.Success, getStatus);
            Assert.Contains(sop.UID, received);
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    [Fact]
    public async Task SrStorageCommitment_VerifiesAndPersistsSuccess()
    {
        var (study, series, sop) = NewUids();
        var filePath = await StoreSrAsync(study, series, sop);
        try
        {
            var transactionUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            var referenced = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.BasicTextSRStorage.UID },
                { DicomTag.ReferencedSOPInstanceUID, sop.UID }
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

            var persisted = await _fx.WaitForAsync(async () =>
            {
                var status = await _fx.QueryScalarAsync<string>(
                    "SELECT Status FROM StorageCommitments WHERE TransactionUid = @t", new { t = transactionUid });
                var failed = await _fx.QueryScalarAsync<int?>(
                    "SELECT FailedCount FROM StorageCommitments WHERE TransactionUid = @t", new { t = transactionUid });
                return status == "Success" && failed == 0;
            }, 20000);
            Assert.True(persisted, "SR storage commitment did not reach Success");
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    [Fact]
    public async Task KeyObjectSelection_StoresQueriesAndLinksKeyImage()
    {
        var (imageSop, imageStudy, imageSeries) = await StoreMinimalImageAsync();

        var (study, series, sop) = NewUids();
        var kos = TestData.CreateMinimalKos(study.UID, series.UID, sop.UID, imageStudy, imageSeries, imageSop);
        var filePath = Path.Combine(Path.GetTempPath(), "kos_" + Guid.NewGuid().ToString("N") + ".dcm");
        new DicomFile(kos).Save(filePath);

        try
        {
            DicomStatus? status = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(filePath);
            request.OnResponseReceived += (_, r) => status = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, status);

            Assert.True(await _fx.WaitForFileAsync(sop.UID + ".dcm", 15000), "stored KOS file not found");
            Assert.True(await _fx.WaitForAsync(
                async () => await _fx.QueryCountAsync(
                    "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }) > 0,
                20000), "KOS was not persisted to the database");

            // 按研究列出 KOS
            using (var list = await GetAsync($"/api/Sr/key-objects?studyInstanceUid={study.UID}", "application/json"))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                Assert.Contains(sop.UID, await list.Content.ReadAsStringAsync());
            }

            // KOS 引用的关键图像
            using (var references = await GetAsync($"/api/Sr/{sop.UID}/references", "application/json"))
            {
                Assert.Equal(HttpStatusCode.OK, references.StatusCode);
                Assert.Contains(imageSop, await references.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            DeleteTemp(filePath);
        }
    }

    private async Task<(string Sop, string Study, string Series)> StoreMinimalImageAsync()
    {
        var (imagePath, imageSop, imageStudy, imageSeries) = TestData.CreateMinimalImage();
        try
        {
            DicomStatus? status = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(imagePath);
            request.OnResponseReceived += (_, r) => status = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, status);

            Assert.True(await _fx.WaitForAsync(
                async () => await _fx.QueryCountAsync(
                    "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = imageSop }) > 0,
                20000), "referenced image was not persisted to the database");
            return (imageSop, imageStudy, imageSeries);
        }
        finally
        {
            var dir = Path.GetDirectoryName(imagePath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private async Task<string> StoreSrAsync(DicomUID study, DicomUID series, DicomUID sop,
        (string Study, string Series, string Sop)? evidence = null)
    {
        var filePath = Path.Combine(Path.GetTempPath(), "sr_" + Guid.NewGuid().ToString("N") + ".dcm");
        new DicomFile(CreateMinimalSr(study, series, sop, evidence)).Save(filePath);

        DicomStatus? storeStatus = null;
        var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
        var cstore = new DicomCStoreRequest(filePath);
        cstore.OnResponseReceived += (_, r) => storeStatus = r.Status;
        await store.AddRequestAsync(cstore);
        await store.SendAsync();
        Assert.Equal(DicomStatus.Success, storeStatus);

        Assert.True(await _fx.WaitForFileAsync(sop.UID + ".dcm", 15000), "stored SR file not found");

        // 等待异步批量入库落库，确保后续 QIDO/WADO/C-FIND 能检索到
        Assert.True(await _fx.WaitForAsync(
            async () => await _fx.QueryCountAsync(
                "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sop.UID }) > 0,
            20000), "SR instance was not persisted to the database");
        return filePath;
    }

    private async Task<bool> WaitForQrHitAsync(string studyUid, int timeoutMs = 20000)
    {
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
        }, timeoutMs);
        return qrHit;
    }

    private async Task<bool> ImageLevelFindHitsAsync(DicomUID study, DicomUID series, DicomUID sop, string documentTitle, int timeoutMs = 20000)
    {
        bool hit = false;
        await _fx.WaitForAsync(async () =>
        {
            var qr = _fx.CreateClient(E2EFixture.QrPort, "QRSCP");
            var cfind = new DicomCFindRequest(
                DicomUID.StudyRootQueryRetrieveInformationModelFind,
                DicomQueryRetrieveLevel.Image,
                DicomPriority.Medium);
            cfind.Dataset = new DicomDataset
            {
                { DicomTag.QueryRetrieveLevel, "IMAGE" },
                { DicomTag.StudyInstanceUID, study.UID },
                { DicomTag.SeriesInstanceUID, series.UID },
                { DicomTag.DocumentTitle, documentTitle }
            };
            cfind.OnResponseReceived += (_, r) =>
            {
                if (r.Status == DicomStatus.Pending)
                {
                    hit |= r.Dataset?.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, "") == sop.UID;
                }
            };
            await qr.AddRequestAsync(cfind);
            await qr.SendAsync();
            return hit;
        }, timeoutMs);
        return hit;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? accept = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(accept))
        {
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
        }
        return await _fx.Http.SendAsync(request);
    }

    private static (DicomUID Study, DicomUID Series, DicomUID Sop) NewUids() =>
        (DicomUIDGenerator.GenerateDerivedFromUUID(),
         DicomUIDGenerator.GenerateDerivedFromUUID(),
         DicomUIDGenerator.GenerateDerivedFromUUID());

    private static void DeleteTemp(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static DicomDataset CreateMinimalSr(DicomUID study, DicomUID series, DicomUID sop,
        (string Study, string Series, string Sop)? evidence = null)
    {
        // 标准 Basic Text SR：顶层数据集即根 CONTAINER，ContentSequence 承载子内容项
        var textConceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        textConceptSeq.Items.Add(new DicomDataset
        {
            { DicomTag.CodeValue, "121106" },
            { DicomTag.CodingSchemeDesignator, "DCM" },
            { DicomTag.CodeMeaning, "Comment" }
        });

        var textItem = new DicomDataset
        {
            { DicomTag.RelationshipType, "CONTAINS" },
            { DicomTag.ValueType, "TEXT" },
            { DicomTag.TextValue, "E2E findings." }
        };
        textItem.Add(DicomTag.ConceptNameCodeSequence, textConceptSeq);

        var contentSeq = new DicomSequence(DicomTag.ContentSequence);
        contentSeq.Items.Add(textItem);

        var rootConceptSeq = new DicomSequence(DicomTag.ConceptNameCodeSequence);
        rootConceptSeq.Items.Add(new DicomDataset
        {
            { DicomTag.CodeValue, "18748-4" },
            { DicomTag.CodingSchemeDesignator, "LN" },
            { DicomTag.CodeMeaning, "Diagnostic imaging study" }
        });

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
        result.AddOrUpdate(DicomTag.ValueType, "CONTAINER");
        result.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");
        // 报告级 SR 属性（用于专属查询键验证）
        result.AddOrUpdate(DicomTag.DocumentTitle, "E2E Structured Report");
        result.AddOrUpdate(DicomTag.CompletionFlag, "COMPLETE");
        result.AddOrUpdate(DicomTag.VerificationFlag, "UNVERIFIED");
        result.AddOrUpdate(DicomTag.VerificationDateTime, "20240101120000");
        result.AddOrUpdate(DicomTag.ContentDate, "20240101");
        result.AddOrUpdate(DicomTag.ContentTime, "120000");
        result.Add(DicomTag.ConceptNameCodeSequence, rootConceptSeq);
        result.Add(DicomTag.ContentSequence, contentSeq);

        if (evidence is { } ev)
        {
            var sopItem = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage.UID },
                { DicomTag.ReferencedSOPInstanceUID, ev.Sop }
            };
            var sopSeq = new DicomSequence(DicomTag.ReferencedSOPSequence);
            sopSeq.Items.Add(sopItem);

            var seriesItem = new DicomDataset { { DicomTag.SeriesInstanceUID, ev.Series } };
            seriesItem.Add(DicomTag.ReferencedSOPSequence, sopSeq);
            var seriesSeq = new DicomSequence(DicomTag.ReferencedSeriesSequence);
            seriesSeq.Items.Add(seriesItem);

            var studyItem = new DicomDataset { { DicomTag.StudyInstanceUID, ev.Study } };
            studyItem.Add(DicomTag.ReferencedSeriesSequence, seriesSeq);
            var studySeq = new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence);
            studySeq.Items.Add(studyItem);

            result.Add(DicomTag.CurrentRequestedProcedureEvidenceSequence, studySeq);
        }

        return result;
    }
}
