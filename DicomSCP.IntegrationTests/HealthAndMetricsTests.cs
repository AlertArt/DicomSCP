using System.Net;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

/// <summary>
/// 健康检查与指标端点（B7）：/health 匿名可用；/api/Metrics 受鉴权保护并导出计数器。
/// </summary>
[Collection("E2E")]
public class HealthAndMetricsTests
{
    private readonly E2EFixture _fx;

    public HealthAndMetricsTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Fact]
    public async Task Health_IsAnonymousAndHealthy()
    {
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2EFixture.HttpPort}") };
        var resp = await anon.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Healthy", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_RequiresAuth()
    {
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{E2EFixture.HttpPort}") };
        var resp = await anon.GetAsync("/api/Metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Metrics_ExportCounters_AndIncreaseAfterCStore()
    {
        // 先取基线
        var before = ParseMetric(await _fx.Http.GetStringAsync("/api/Metrics"), "dicomscp_cstore_success_total");

        // 执行一次 C-STORE
        var (filePath, _, _, _) = TestData.CreateMinimalImage();
        try
        {
            DicomStatus? status = null;
            var store = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
            var request = new DicomCStoreRequest(filePath);
            request.OnResponseReceived += (_, r) => status = r.Status;
            await store.AddRequestAsync(request);
            await store.SendAsync();
            Assert.Equal(DicomStatus.Success, status);
        }
        finally
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        var text = await _fx.Http.GetStringAsync("/api/Metrics");
        Assert.Contains("dicomscp_cstore_received_total", text);
        var after = ParseMetric(text, "dicomscp_cstore_success_total");
        Assert.True(after > before, $"cstore_success_total did not increase ({before} -> {after})");

        // JSON 摘要端点
        var summary = await _fx.Http.GetStringAsync("/api/Metrics/summary");
        Assert.Contains("counters", summary);
        Assert.Contains("uptimeSeconds", summary);
    }

    private static long ParseMetric(string prometheusText, string name)
    {
        foreach (var line in prometheusText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(name + " ", StringComparison.Ordinal))
            {
                return long.TryParse(trimmed[(name.Length + 1)..].Trim(), out var value) ? value : -1;
            }
        }
        return -1;
    }
}
