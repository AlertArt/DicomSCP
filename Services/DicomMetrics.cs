using System.Collections.Concurrent;

namespace DicomSCP.Services;

/// <summary>
/// 轻量运行指标注册表（Prometheus 风格）：计数器 + 仪表。
/// 服务端各 SCP 与入库管线通过静态方法埋点，运维端点导出。
/// </summary>
public static class DicomMetrics
{
    // 计数器名称（Prometheus 标识符）
    public const string CStoreReceived = "dicomscp_cstore_received_total";
    public const string CStoreSuccess = "dicomscp_cstore_success_total";
    public const string CStoreFailure = "dicomscp_cstore_failure_total";
    public const string CStoreDuplicate = "dicomscp_cstore_duplicate_total";

    public const string CFind = "dicomscp_cfind_total";
    public const string CMove = "dicomscp_cmove_total";
    public const string CGet = "dicomscp_cget_total";

    public const string StorageCommitRequest = "dicomscp_storage_commit_request_total";
    public const string StorageCommitFailure = "dicomscp_storage_commit_failure_total";
    public const string StorageCommitNotificationFailed = "dicomscp_storage_commit_notification_failed_total";

    public const string DbBatchProcessed = "dicomscp_db_batch_processed_total";
    public const string DbInsertFailure = "dicomscp_db_insert_failure_total";
    public const string DbRetry = "dicomscp_db_retry_total";

    // 仪表名称
    public const string DbQueueDepth = "dicomscp_db_queue_depth";

    private static readonly ConcurrentDictionary<string, long> _counters = new();
    private static readonly ConcurrentDictionary<string, long> _gauges = new();
    private static readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>计数器自增。</summary>
    public static void Increment(string name, long delta = 1)
    {
        if (delta == 0) return;
        _counters.AddOrUpdate(name, delta, (_, current) => current + delta);
    }

    /// <summary>设置仪表值。</summary>
    public static void SetGauge(string name, long value) => _gauges[name] = value;

    public static IReadOnlyDictionary<string, long> Counters => _counters;
    public static IReadOnlyDictionary<string, long> Gauges => _gauges;

    public static TimeSpan Uptime => DateTime.UtcNow - _startedAt;

    /// <summary>测试用：清空所有指标。</summary>
    public static void Reset()
    {
        _counters.Clear();
        _gauges.Clear();
    }
}
