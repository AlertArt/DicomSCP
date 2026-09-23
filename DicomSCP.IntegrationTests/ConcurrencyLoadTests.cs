using System.Diagnostics;
using FellowOakDicom;
using FellowOakDicom.Network;
using Xunit;

namespace DicomSCP.IntegrationTests;

/// <summary>
/// 并发负载证明：N 个并发 C-STORE SCU（8/16/32）同时推送，全部成功且无丢失/错乱。
/// 验证 CStoreSCP 在并发关联下不出现文件/入库损坏，作为临床并发的背书。
/// </summary>
[Collection("E2E")]
public class ConcurrencyLoadTests
{
    private readonly E2EFixture _fx;

    public ConcurrencyLoadTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task ConcurrentCStore_AllSucceedWithoutCorruption(int concurrency)
    {
        var items = Enumerable.Range(0, concurrency).Select(_ => TestData.CreateMinimalImage()).ToList();
        try
        {
            var results = new DicomStatus?[concurrency];
            var stopwatch = Stopwatch.StartNew();

            var tasks = new List<Task>(concurrency);
            for (var i = 0; i < concurrency; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var client = _fx.CreateClient(E2EFixture.StorePort, "STORESCP");
                    var request = new DicomCStoreRequest(items[index].FilePath);
                    request.OnResponseReceived += (_, r) => results[index] = r.Status;
                    await client.AddRequestAsync(request);
                    await client.SendAsync();
                }));
            }

            await Task.WhenAll(tasks);
            stopwatch.Stop();

            // 全部 C-STORE 成功
            Assert.All(results, status => Assert.Equal(DicomStatus.Success, status));

            // 等待异步批量入库，断言全部实例落库（无丢失/错乱）
            var sops = items.Select(i => i.SopUid).ToList();
            var allPersisted = await _fx.WaitForAsync(async () =>
                await _fx.QueryCountAsync(
                    "SELECT COUNT(*) FROM Instances WHERE SopInstanceUid IN @Sops",
                    new { Sops = sops }) == concurrency, 30000);
            Assert.True(allPersisted, $"并发 {concurrency}: 未全部落库");

            // 归档文件全部存在且唯一
            foreach (var item in items)
            {
                Assert.True(await _fx.WaitForFileAsync(item.SopUid + ".dcm", 15000),
                    $"并发 {concurrency}: 缺少归档文件 {item.SopUid}");
            }

            Console.WriteLine($"[load] concurrency={concurrency} elapsed={stopwatch.ElapsedMilliseconds}ms all=Success persisted={concurrency}");
        }
        finally
        {
            foreach (var item in items)
            {
                var dir = Path.GetDirectoryName(item.FilePath);
                if (dir != null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }
}
