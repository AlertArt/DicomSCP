using Dapper;
using DicomSCP.Models;
using DicomSCP.Services;
using FellowOakDicom;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DicomSCP.Repository;

/// <summary>
/// 负责 DICOM 数据集解析与批量入库（含队列批处理）。
/// </summary>
public sealed class DicomDatasetPersistence : IDisposable
{
    private readonly string _connectionString;
    private sealed class QueueItem
    {
        public required DicomDataset Dataset { get; init; }
        public required string FilePath { get; init; }
        public int RetryCount { get; set; }
    }

    private sealed class FailedRecord
    {
        public string StudyInstanceUid { get; set; } = "";
        public string SeriesInstanceUid { get; set; } = "";
        public string SopInstanceUid { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int RetryCount { get; set; }
        public DateTime FailedAt { get; set; }
    }

    private readonly ConcurrentQueue<QueueItem> _dataQueue = new();
    private readonly SemaphoreSlim _processSemaphore = new(1, 1);
    private readonly Timer _processTimer;
    private readonly int _batchSize;
    private readonly int _maxRetryCount;
    private readonly string _failedQueuePath;
    private readonly TimeSpan _maxWaitTime = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _minWaitTime = TimeSpan.FromSeconds(2);
    private readonly Stopwatch _performanceTimer = new();
    private DateTime _lastProcessTime = DateTime.Now;
    private bool _disposed;

    public DicomDatasetPersistence(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DicomDb")
            ?? throw new ArgumentException("Missing DicomDb connection string");
        _batchSize = configuration.GetValue<int>("DicomSettings:BatchSize", 50);
        if (_batchSize <= 0) _batchSize = 50;
        _maxRetryCount = configuration.GetValue<int>("DicomSettings:MaxRetryCount", 3);
        if (_maxRetryCount < 0) _maxRetryCount = 3;
        _failedQueuePath = configuration.GetValue<string>("DicomSettings:FailedQueuePath") ?? "./failed_queue";
        _processTimer = new Timer(async _ => await ProcessQueueAsync(), null, _minWaitTime, _minWaitTime);
    }

    private static class SqlQueries
    {
        public const string InsertPatient = @"
            INSERT OR IGNORE INTO Patients 
            (PatientId, PatientName, PatientBirthDate, PatientSex, CreateTime)
            VALUES (@PatientId, @PatientName, @PatientBirthDate, @PatientSex, @CreateTime)";

        public const string InsertStudy = @"
            INSERT OR IGNORE INTO Studies 
            (StudyInstanceUid, PatientId, StudyDate, StudyTime, StudyDescription, 
             AccessionNumber, Modality, InstitutionName, CreateTime)
            VALUES (@StudyInstanceUid, @PatientId, @StudyDate, @StudyTime, @StudyDescription, 
                    @AccessionNumber, @Modality, @InstitutionName, @CreateTime)";

        public const string InsertSeries = @"
            INSERT OR IGNORE INTO Series 
            (
                SeriesInstanceUid, 
                StudyInstanceUid, 
                Modality, 
                SeriesNumber, 
                SeriesDescription,
                SliceThickness,
                SeriesDate,        
                CreateTime
            )
            VALUES 
            (
                @SeriesInstanceUid, 
                @StudyInstanceUid, 
                @Modality, 
                @SeriesNumber, 
                @SeriesDescription,
                @SliceThickness,
                @SeriesDate,
                @CreateTime
            )";

        public const string InsertInstance = @"
            INSERT OR IGNORE INTO Instances (
                SopInstanceUid, SeriesInstanceUid, SopClassUid, InstanceNumber, FilePath,
                Columns, Rows, PhotometricInterpretation, BitsAllocated, BitsStored,
                PixelRepresentation, SamplesPerPixel, PixelSpacing, HighBit,
                ImageOrientationPatient, ImagePositionPatient, FrameOfReferenceUID,
                ImageType, WindowCenter, WindowWidth, CreateTime
            ) VALUES (
                @SopInstanceUid, @SeriesInstanceUid, @SopClassUid, @InstanceNumber, @FilePath,
                @Columns, @Rows, @PhotometricInterpretation, @BitsAllocated, @BitsStored,
                @PixelRepresentation, @SamplesPerPixel, @PixelSpacing, @HighBit,
                @ImageOrientationPatient, @ImagePositionPatient, @FrameOfReferenceUID,
                @ImageType, @WindowCenter, @WindowWidth, @CreateTime
            )";
    }

    public sealed class BatchData
    {
        public List<Patient> Patients { get; } = [];
        public List<Study> Studies { get; } = [];
        public List<Series> Series { get; } = [];
        public List<Instance> Instances { get; } = [];
        public bool HasData => Patients.Count > 0 || Studies.Count > 0 || Series.Count > 0 || Instances.Count > 0;
    }

    public sealed record WriteResult(int InsertedPatients, int InsertedStudies, int InsertedSeries, int InsertedInstances);

    /// <summary>
    /// 对外入口：将数据集加入入库队列。
    /// </summary>
    public async Task SaveDicomDataAsync(DicomDataset dataset, string filePath)
    {
        _dataQueue.Enqueue(new QueueItem { Dataset = dataset, FilePath = filePath });

        // 当队列达到批处理的80%时，主动触发处理
        if (_dataQueue.Count >= _batchSize * 0.8)
        {
            await ProcessQueueAsync();
        }
    }

    /// <summary>
    /// 立即同步入库（绕过异步队列），供 STOW-RS 等需要请求完成后即可被
    /// QIDO-RS/WADO-RS 查询到的场景使用。
    /// </summary>
    public async Task<WriteResult> SaveDicomDataImmediateAsync(DicomDataset dataset, string filePath)
    {
        return await SaveDicomDataImmediateAsync(new[] { (dataset, filePath) });
    }

    public async Task<WriteResult> SaveDicomDataImmediateAsync(IEnumerable<(DicomDataset Dataset, string FilePath)> items)
    {
        var itemList = items.ToList();
        if (itemList.Count == 0)
        {
            return new WriteResult(0, 0, 0, 0);
        }

        var batchData = BuildBatchData(itemList, DateTime.Now);
        if (!batchData.HasData)
        {
            DicomLogger.Warning("Database", "[DB] 立即入库：批处理中没有有效数据");
            return new WriteResult(0, 0, 0, 0);
        }

        await using var connection = SqliteConnectionFactory.Create(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var writeResult = await InsertBatchAsync(connection, transaction, batchData);
            await transaction.CommitAsync();
            return writeResult;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            DicomLogger.Error("Database", ex, "[DB] 立即入库失败 - 数据条数: {Count}", itemList.Count);
            return new WriteResult(0, 0, 0, 0);
        }
    }

    private async Task ProcessQueueAsync()
    {
        if (_dataQueue.IsEmpty) return;

        var queueSize = _dataQueue.Count;
        var waitTime = DateTime.Now - _lastProcessTime;

        // 修改处理时机判断
        if (queueSize >= _batchSize || // 队列达到批处理大小
            (queueSize > 0 && waitTime >= _maxWaitTime) || // 等待超过10秒就处理
            (queueSize >= 5 && waitTime >= _minWaitTime))  // 只要有5条且等待超过2秒就处理
        {
            await ProcessBatchWithRetryAsync();
        }
    }

    private async Task ProcessBatchWithRetryAsync()
    {
        if (!await _processSemaphore.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            return;
        }

        List<QueueItem> batchItems = new();

        try
        {
            _performanceTimer.Restart();
            var batchSize = Math.Min(_dataQueue.Count, _batchSize);

            // 一次性收集批处理数据
            while (batchItems.Count < batchSize && _dataQueue.TryDequeue(out var item))
            {
                batchItems.Add(item);
            }

            if (batchItems.Count == 0) return;

            await using var connection = SqliteConnectionFactory.Create(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var now = DateTime.Now;
                var batchData = BuildBatchData(
                    batchItems.Select(i => (i.Dataset, i.FilePath)).ToList(), now);
                if (!batchData.HasData)
                {
                    DicomLogger.Warning("Database", "[DB] 批处理中没有有效数据");
                    return;
                }

                var writeResult = await InsertBatchAsync(connection, transaction, batchData);
                await transaction.CommitAsync();

                _performanceTimer.Stop();
                _lastProcessTime = DateTime.Now;

                DicomLogger.Information(
                    "Database",
                    "[DB] 批量处理完成 - 总数: {Count}, 新增: P={Patients}, S={Studies}, Se={Series}, I={Instances}, 耗时: {Time}ms, 队列剩余: {Remaining}",
                    batchItems.Count,
                    writeResult.InsertedPatients,
                    writeResult.InsertedStudies,
                    writeResult.InsertedSeries,
                    writeResult.InsertedInstances,
                    _performanceTimer.ElapsedMilliseconds,
                    _dataQueue.Count);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                DicomLogger.Error("Database", ex, "[DB] 数据库操作失败 - 批次大小: {Count}", batchItems.Count);

                // 失败批次自动重试（带上限），超限后落盘到失败队列目录，避免数据丢失
                foreach (var item in batchItems)
                {
                    if (item.RetryCount < _maxRetryCount)
                    {
                        item.RetryCount++;
                        _dataQueue.Enqueue(item);
                        DicomLogger.Warning("Database",
                            "[DB] 数据入库失败，重新入队 - 文件: {FilePath}, 第 {RetryCount}/{MaxRetry} 次重试",
                            item.FilePath, item.RetryCount, _maxRetryCount);
                    }
                    else
                    {
                        PersistFailedItem(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Database", ex, "[DB] 处理批次时发生异常");
        }
        finally
        {
            _processSemaphore.Release();
        }
    }

    public BatchData BuildBatchData(List<(DicomDataset Dataset, string FilePath)> batchItems, DateTime now)
    {
        var batchData = new BatchData();
        batchData.Patients.Capacity = batchItems.Count;
        batchData.Studies.Capacity = batchItems.Count;
        batchData.Series.Capacity = batchItems.Count;
        batchData.Instances.Capacity = batchItems.Count;

        foreach (var (dataset, filePath) in batchItems)
        {
            try
            {
                ExtractDicomData(dataset, filePath, now, batchData);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("Database", ex, "[DB] 处理DICOM数据失败 - 文件: {FilePath}", filePath);
            }
        }

        return batchData;
    }

    public async Task<WriteResult> InsertBatchAsync(SqliteConnection connection, IDbTransaction transaction, BatchData batchData)
    {
        var insertedPatients = await connection.ExecuteAsync(SqlQueries.InsertPatient, batchData.Patients, transaction);
        var insertedStudies = await connection.ExecuteAsync(SqlQueries.InsertStudy, batchData.Studies, transaction);
        var insertedSeries = await connection.ExecuteAsync(SqlQueries.InsertSeries, batchData.Series, transaction);
        var insertedInstances = await connection.ExecuteAsync(SqlQueries.InsertInstance, batchData.Instances, transaction);
        return new WriteResult(insertedPatients, insertedStudies, insertedSeries, insertedInstances);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _processTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _processTimer?.Dispose();

            // 尝试同步刷完剩余队列
            while (!_dataQueue.IsEmpty)
            {
                if (_processSemaphore.Wait(TimeSpan.FromSeconds(30)))
                {
                    try
                    {
                        ProcessBatchWithRetryAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        _processSemaphore.Release();
                    }
                }
                else
                {
                    break;
                }
            }

            _processSemaphore.Dispose();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Database", ex, "[DB] Dispose过程发生错误");
        }
    }

    /// <summary>
    /// 将重试已达上限的入库项落盘到失败队列目录，供启动时重放或人工处理。
    /// </summary>
    private void PersistFailedItem(QueueItem item)
    {
        try
        {
            Directory.CreateDirectory(_failedQueuePath);
            var record = new FailedRecord
            {
                StudyInstanceUid = item.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, "Unknown"),
                SeriesInstanceUid = item.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, "Unknown"),
                SopInstanceUid = item.Dataset.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, "Unknown"),
                FilePath = item.FilePath,
                RetryCount = item.RetryCount,
                FailedAt = DateTime.Now
            };

            var fileName = $"{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.json";
            File.WriteAllText(Path.Combine(_failedQueuePath, fileName), JsonSerializer.Serialize(record));
            DicomLogger.Error("Database",
                "[DB] 数据入库失败已达上限（{MaxRetry}次），已落盘到失败队列 - 文件: {FilePath}, 记录: {Record}",
                _maxRetryCount, item.FilePath, fileName);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Database", ex, "[DB] 落盘失败记录时出错 - 文件: {FilePath}", item.FilePath);
        }
    }

    /// <summary>
    /// 启动时扫描失败队列目录并重放：读取归档文件重建数据集后立即入库，
    /// 入库（或幂等确认已存在）成功的记录文件被删除，失败则保留待下次重放。
    /// </summary>
    public async Task TryReplayFailedQueueAsync(string storageRoot)
    {
        if (!Directory.Exists(_failedQueuePath))
        {
            return;
        }

        var recordFiles = Directory.EnumerateFiles(_failedQueuePath, "*.json").ToList();
        if (recordFiles.Count > 0)
        {
            DicomLogger.Information("Database", "[DB] 发现失败队列记录 {Count} 条，开始重放", recordFiles.Count);
        }

        foreach (var recordFile in recordFiles)
        {
            try
            {
                var record = JsonSerializer.Deserialize<FailedRecord>(await File.ReadAllTextAsync(recordFile));
                if (record == null || string.IsNullOrEmpty(record.FilePath))
                {
                    continue;
                }

                var fullPath = Path.Combine(storageRoot, record.FilePath.Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    DicomLogger.Warning("Database", "[DB] 失败队列中的文件已不存在，跳过 - 文件: {FilePath}", record.FilePath);
                    continue;
                }

                var dicomFile = await DicomFile.OpenAsync(fullPath);
                // 入库采用 INSERT OR IGNORE，语义幂等：已存在（0条新增）同样视为重放完成
                await SaveDicomDataImmediateAsync(dicomFile.Dataset, record.FilePath);
                File.Delete(recordFile);
                DicomLogger.Information("Database", "[DB] 失败队列重放完成 - 文件: {FilePath}", record.FilePath);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("Database", ex, "[DB] 失败队列重放失败 - 记录文件: {File}", recordFile);
            }
        }
    }

    private static void ExtractDicomData(
        DicomDataset dataset,
        string filePath,
        DateTime now,
        BatchData batchData)
    {
        var patientId = dataset.GetSingleValue<string>(DicomTag.PatientID);
        var studyInstanceUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
        var seriesInstanceUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);

        batchData.Patients.Add(new Patient
        {
            PatientId = patientId,
            PatientName = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty),
            PatientBirthDate = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientBirthDate, string.Empty),
            PatientSex = dataset.GetSingleValueOrDefault<string>(DicomTag.PatientSex, string.Empty),
            CreateTime = now
        });

        batchData.Studies.Add(new Study
        {
            StudyInstanceUid = studyInstanceUid,
            PatientId = patientId,
            StudyDate = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty),
            StudyTime = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty),
            StudyDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDescription, string.Empty),
            AccessionNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty),
            Modality = GetStudyModality(dataset),
            InstitutionName = dataset.GetSingleValueOrDefault<string>(DicomTag.InstitutionName, string.Empty),
            CreateTime = now
        });

        batchData.Series.Add(new Series
        {
            SeriesInstanceUid = seriesInstanceUid,
            StudyInstanceUid = studyInstanceUid,
            Modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty),
            SeriesNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesNumber, string.Empty),
            SeriesDescription = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesDescription, string.Empty),
            SliceThickness = dataset.GetSingleValueOrDefault<string>(DicomTag.SliceThickness, string.Empty),
            SeriesDate = dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesDate, string.Empty),
            CreateTime = now
        });

        batchData.Instances.Add(new Instance
        {
            SopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
            SeriesInstanceUid = seriesInstanceUid,
            SopClassUid = dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
            InstanceNumber = dataset.GetSingleValueOrDefault<string>(DicomTag.InstanceNumber, string.Empty),
            FilePath = filePath,
            Columns = dataset.GetSingleValueOrDefault<int>(DicomTag.Columns, 0),
            Rows = dataset.GetSingleValueOrDefault<int>(DicomTag.Rows, 0),
            PhotometricInterpretation = dataset.GetSingleValueOrDefault<string>(DicomTag.PhotometricInterpretation, string.Empty),
            BitsAllocated = dataset.GetSingleValueOrDefault<int>(DicomTag.BitsAllocated, 0),
            BitsStored = dataset.GetSingleValueOrDefault<int>(DicomTag.BitsStored, 0),
            PixelRepresentation = dataset.GetSingleValueOrDefault<int>(DicomTag.PixelRepresentation, 0),
            SamplesPerPixel = dataset.GetSingleValueOrDefault<int>(DicomTag.SamplesPerPixel, 0),
            PixelSpacing = dataset.Contains(DicomTag.PixelSpacing)
                ? (dataset.GetValueCount(DicomTag.PixelSpacing) > 1
                    ? string.Join("\\", dataset.GetValues<decimal>(DicomTag.PixelSpacing))
                    : dataset.GetSingleValueOrDefault<decimal>(DicomTag.PixelSpacing, 0).ToString())
                : string.Empty,
            HighBit = dataset.GetSingleValueOrDefault<int>(DicomTag.HighBit, 0),
            ImageOrientationPatient = dataset.Contains(DicomTag.ImageOrientationPatient)
                ? (dataset.GetValueCount(DicomTag.ImageOrientationPatient) > 1
                    ? string.Join("\\", dataset.GetValues<decimal>(DicomTag.ImageOrientationPatient))
                    : dataset.GetSingleValueOrDefault<decimal>(DicomTag.ImageOrientationPatient, 0).ToString())
                : string.Empty,
            ImagePositionPatient = dataset.Contains(DicomTag.ImagePositionPatient)
                ? (dataset.GetValueCount(DicomTag.ImagePositionPatient) > 1
                    ? string.Join("\\", dataset.GetValues<decimal>(DicomTag.ImagePositionPatient))
                    : dataset.GetSingleValueOrDefault<decimal>(DicomTag.ImagePositionPatient, 0).ToString())
                : string.Empty,
            FrameOfReferenceUID = dataset.GetSingleValueOrDefault<string>(DicomTag.FrameOfReferenceUID, string.Empty),
            ImageType = dataset.Contains(DicomTag.ImageType)
                ? (dataset.GetValueCount(DicomTag.ImageType) > 1
                    ? string.Join("\\", dataset.GetValues<string>(DicomTag.ImageType))
                    : dataset.GetSingleValueOrDefault<string>(DicomTag.ImageType, string.Empty))
                : string.Empty,
            WindowCenter = dataset.Contains(DicomTag.WindowCenter)
                ? (dataset.GetValueCount(DicomTag.WindowCenter) > 1
                    ? string.Join("\\", dataset.GetValues<string>(DicomTag.WindowCenter))
                    : dataset.GetSingleValueOrDefault<string>(DicomTag.WindowCenter, string.Empty))
                : string.Empty,
            WindowWidth = dataset.Contains(DicomTag.WindowWidth)
                ? (dataset.GetValueCount(DicomTag.WindowWidth) > 1
                    ? string.Join("\\", dataset.GetValues<string>(DicomTag.WindowWidth))
                    : dataset.GetSingleValueOrDefault<string>(DicomTag.WindowWidth, string.Empty))
                : string.Empty,
            CreateTime = now
        });
    }

    private static string GetStudyModality(DicomDataset dataset)
    {
        try
        {
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                var modalities = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                if (modalities is { Length: > 0 })
                {
                    return string.Join("\\", modalities.Where(m => !string.IsNullOrEmpty(m)));
                }
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Warning("Database", "[DB] 获取ModalitiesInStudy失败: {Error}", ex.Message);
        }

        var modality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
        return string.IsNullOrEmpty(modality) ? string.Empty : modality;
    }
}
