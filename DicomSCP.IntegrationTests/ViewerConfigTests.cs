using System.Net;
using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

/// <summary>
/// 查看器装配（不依赖浏览器）：
/// - OHIF 应用外壳与配置可访问，且配置指向本地 /dicomweb；
/// - Weasis 外部客户端一次性令牌：签发需登录，令牌可免会话访问 manifest 与 /wado 且限定研究。
/// </summary>
[Collection("E2E")]
public class ViewerConfigTests
{
    private readonly E2EFixture _fx;

    public ViewerConfigTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task OhifAppShell_IsServed()
    {
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2EFixture.HttpPort}") };
        var resp = await anon.GetAsync("/dicomviewer/index.html");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task OhifConfig_PointsToLocalDicomweb()
    {
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2EFixture.HttpPort}") };
        var config = await anon.GetStringAsync("/dicomviewer/app-config.js");

        Assert.Contains("/dicomweb", config);
        Assert.DoesNotContain("cloudfront", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/wado", config);
    }

    [Fact]
    public async Task WeasisToken_IsRequiredForExternalAccess_AndScopedToStudy()
    {
        var (filePath, sopUid, studyUid, _) = TestData.CreateMinimalImage();
        try
        {
            // 归档一个研究以生成 manifest
            DicomStatus? storeStatus = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(filePath);
            request.OnResponseReceived += (_, r) => storeStatus = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, storeStatus);

            // 等待异步批量入库，确保 manifest 查询能命中研究
            Assert.True(await _fx.WaitForAsync(async () =>
                await _fx.QueryCountAsync(
                    "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid = @Sop", new { Sop = sopUid }) > 0,
                20000), "instance not persisted");

            using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2EFixture.HttpPort}") };

            // 无令牌：外部访问被拒
            var noToken = await anon.GetAsync($"/viewer/weasis/{studyUid}");
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

            // 签发令牌需登录
            var anonToken = await anon.GetAsync($"/api/Weasis/token?studyInstanceUid={studyUid}");
            Assert.Equal(HttpStatusCode.Unauthorized, anonToken.StatusCode);

            // 已登录签发令牌
            var issued = await _fx.Http.GetAsync($"/api/Weasis/token?studyInstanceUid={studyUid}");
            Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
            using var doc = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
            var token = doc.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrEmpty(token));

            // 令牌可免会话获取 manifest
            var manifest = await anon.GetAsync($"/viewer/weasis/{studyUid}?token={token}");
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);

            // 令牌限定研究：用于其它研究无效
            var otherStudy = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            var mismatched = await anon.GetAsync($"/viewer/weasis/{otherStudy}?token={token}");
            Assert.Equal(HttpStatusCode.Unauthorized, mismatched.StatusCode);

            // 令牌可用于 /wado（不再 401）
            var wado = await anon.GetAsync($"/wado?requestType=WADO&studyUID={studyUid}&seriesUID=1.2.3&objectUID=1.2.3&token={token}");
            Assert.NotEqual(HttpStatusCode.Unauthorized, wado.StatusCode);
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
