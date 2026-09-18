using Dapper;
using DicomSCP.Models;
using FellowOakDicom;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

public class DicomRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{

    public async Task<Study?> GetStudyAsync(string studyInstanceUid)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();
        
        var sql = @"
            SELECT s.*, p.PatientName, p.PatientId, p.PatientSex, p.PatientBirthDate
            FROM Studies s
            LEFT JOIN Patients p ON s.PatientId = p.PatientId
            WHERE s.StudyInstanceUid = @StudyInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Study>(
            sql,
            new { StudyInstanceUid = studyInstanceUid }
        );
    }

    public async Task<IEnumerable<Instance>> GetSeriesInstancesAsync(string seriesInstanceUid)
    {
        using var connection = CreateConnection();
        var sql = @"
            SELECT * FROM Instances 
            WHERE SeriesInstanceUid = @SeriesInstanceUid 
            ORDER BY CAST(InstanceNumber as INTEGER)";
        
        return await connection.QueryAsync<Instance>(sql, new { SeriesInstanceUid = seriesInstanceUid });
    }

    public async Task<Instance?> GetInstanceAsync(string sopInstanceUid)
    {
        using var connection = CreateConnection();
        var sql = "SELECT * FROM Instances WHERE SopInstanceUid = @SopInstanceUid";
        
        return await connection.QueryFirstOrDefaultAsync<Instance>(
            sql, 
            new { SopInstanceUid = sopInstanceUid }
        );
    }

    public List<Series> GetSeriesByStudyUid(string studyInstanceUid, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT s.*, 
                       (SELECT COUNT(*) FROM Instances i WHERE i.SeriesInstanceUid = s.SeriesInstanceUid) as NumberOfInstances
                FROM Series s
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(s.SeriesNumber as INTEGER)";

            LogDebug("执行序列查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var series = connection.Query<Series>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = series?.ToList() ?? new List<Series>();
            LogInformation("序列查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "序列查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            if (throwOnError) throw;
            return [];
        }
    }

    public List<Instance> GetInstancesBySeriesUid(string studyInstanceUid, string seriesInstanceUid, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid 
                AND i.SeriesInstanceUid = @SeriesInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行图像查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                sql, studyInstanceUid, seriesInstanceUid);

            var instances = connection.Query<Instance>(sql, new 
            { 
                StudyInstanceUid = studyInstanceUid,
                SeriesInstanceUid = seriesInstanceUid
            });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("图像查询完成 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, seriesInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "图像查询失败 - StudyInstanceUid: {StudyInstanceUid}, SeriesInstanceUid: {SeriesInstanceUid}", 
                studyInstanceUid, seriesInstanceUid);
            if (throwOnError) throw;
            return new List<Instance>();
        }
    }

    public IEnumerable<Instance> GetInstancesByStudyUid(string studyInstanceUid, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT i.*, s.StudyInstanceUid, s.Modality
                FROM Instances i
                JOIN Series s ON i.SeriesInstanceUid = s.SeriesInstanceUid
                WHERE s.StudyInstanceUid = @StudyInstanceUid
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            LogDebug("执行实例查询 - SQL: {Sql}, StudyInstanceUid: {StudyInstanceUid}", 
                sql, studyInstanceUid);

            var instances = connection.Query<Instance>(sql, new { StudyInstanceUid = studyInstanceUid });

            var result = instances?.ToList() ?? new List<Instance>();
            LogInformation("实例查询完成 - StudyInstanceUid: {StudyInstanceUid}, 返回记录数: {Count}", 
                studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "实例查询失败 - StudyInstanceUid: {StudyInstanceUid}", studyInstanceUid);
            if (throwOnError) throw;
            return [];
        }
    }

    public IEnumerable<Patient> GetPatients(string patientId, string patientName, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    p.PatientId, 
                    p.PatientName, 
                    p.PatientBirthDate, 
                    p.PatientSex,
                    p.CreateTime,
                    COUNT(DISTINCT s.StudyInstanceUid) as NumberOfStudies,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfInstances
                FROM Patients p
                LEFT JOIN Studies s ON p.PatientId = s.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1";

            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(patientId))
            {
                sql += " AND p.PatientId LIKE @PatientId";
                parameters.Add("@PatientId", $"%{patientId}%");
            }

            if (!string.IsNullOrEmpty(patientName))
            {
                sql += " AND p.PatientName LIKE @PatientName";
                parameters.Add("@PatientName", $"%{patientName}%");
            }

            sql += @" 
                GROUP BY p.PatientId, p.PatientName, p.PatientBirthDate, p.PatientSex, p.CreateTime
                ORDER BY p.PatientName";

            LogDebug("执行Patient查询 - SQL: {Sql}, PatientId: {PatientId}, PatientName: {PatientName}", 
                sql, patientId, patientName);

            var patients = connection.Query<Patient>(sql, parameters).ToList();

            LogInformation("Patient查询完成 - 返回记录数: {Count}", patients.Count);

            return patients;
        }
        catch (Exception ex)
        {
            LogError(ex, "Patient查询失败 - PatientId: {PatientId}, PatientName: {PatientName}", 
                patientId, patientName);
            if (throwOnError) throw;
            return Enumerable.Empty<Patient>();
        }
    }

    // 保原有法，供 QRSCP 使用
    public List<Study> GetStudies(
        string patientId, 
        string patientName, 
        string accessionNumber, 
        (string StartDate, string EndDate) dateRange,
        string[]? modalities,
        string? studyInstanceUid = null,
        int? offset = null,
        int? limit = null,
        bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            // 先查询总数
            var countSql = @"
                SELECT COUNT(DISTINCT s.StudyInstanceUid)
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                AND (@StudyInstanceUid = '' OR s.StudyInstanceUid = @StudyInstanceUid)";

            var sql = @"
                SELECT 
                    s.*,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfStudyRelatedSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfStudyRelatedInstances
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1
                AND (@PatientId = '' OR s.PatientId LIKE @PatientId)
                AND (@PatientName = '' OR p.PatientName LIKE @PatientName)
                AND (@AccessionNumber = '' OR s.AccessionNumber LIKE @AccessionNumber)
                AND (@StartDate = '' OR s.StudyDate >= @StartDate)
                AND (@EndDate = '' OR s.StudyDate <= @EndDate)
                AND (@ModCount = 0 OR s.Modality IN @Modalities)
                AND (@StudyInstanceUid = '' OR s.StudyInstanceUid = @StudyInstanceUid)
                GROUP BY 
                    s.StudyInstanceUid,
                    s.PatientId,
                    s.StudyDate,
                    s.StudyTime,
                    s.StudyDescription,
                    s.AccessionNumber,
                    s.Modality,
                    s.CreateTime,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate
                ORDER BY s.CreateTime DESC";

            // 如果指定了分页参数，添加分页
            if (offset.HasValue && limit.HasValue)
            {
                sql += " LIMIT @Limit OFFSET @Offset";
            }

            var parameters = new
            {
                PatientId = string.IsNullOrEmpty(patientId) ? "" : $"%{patientId}%",
                PatientName = string.IsNullOrEmpty(patientName) ? "" : $"%{patientName}%",
                AccessionNumber = string.IsNullOrEmpty(accessionNumber) ? "" : $"%{accessionNumber}%",
                StartDate = dateRange.StartDate,
                EndDate = dateRange.EndDate,
                ModCount = modalities?.Length ?? 0,
                Modalities = (modalities?.Length ?? 0) > 0 ? modalities : [""],
                StudyInstanceUid = studyInstanceUid ?? "",
                Offset = offset,
                Limit = limit
            };

            LogDebug("执行检查查询 - SQL: {Sql}, 参数: {@Parameters}", sql, parameters);

            // 获取总数
            var totalCount = connection.ExecuteScalar<int>(countSql, parameters);

            var studies = connection.Query<Study>(sql, parameters);
            var result = studies?.ToList() ?? [];
            
            LogInformation("检查查询完成 - 返回记录数: {Count}/{Total}, 日期范围: {StartDate} - {EndDate}, StudyInstanceUID: {StudyUID}", 
                result.Count, totalCount, dateRange.StartDate ?? "", dateRange.EndDate ?? "", studyInstanceUid ?? "");

            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "检查查询失败");
            if (throwOnError) throw;
            return [];
        }
    }

    public async Task<IEnumerable<Series>> GetSeriesAsync(string studyInstanceUid)
    {
        using var connection = CreateConnection();
        var sql = @"
            SELECT s.*, COUNT(i.SopInstanceUid) as NumberOfInstances
            FROM Series s
            LEFT JOIN Instances i ON s.SeriesInstanceUid = i.SeriesInstanceUid
            WHERE s.StudyInstanceUid = @StudyInstanceUid
            GROUP BY s.SeriesInstanceUid, s.StudyInstanceUid, s.Modality, 
                     s.SeriesNumber, s.SeriesDescription, s.SliceThickness, 
                     s.SeriesDate, s.CreateTime";

        return await connection.QueryAsync<Series>(sql, new { StudyInstanceUid = studyInstanceUid });
    }

    // ── QIDO-RS (DICOMweb) 查询支持 ─────────────────────────────────────────
    // 为 QIDO-RS 提供 Study / Series / Instance 三种层级的属性过滤查询。
    // 数据库仅持久化常用标签，因此未知标签会被静默忽略（符合 PS3.18 QIDO-RS 约定）。

    public List<Study> QidoQueryStudies(IReadOnlyDictionary<DicomTag, IReadOnlyList<string>> matches, bool fuzzy, int? offset, int? limit, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var (whereSql, parameters) = BuildQidoWhere(QidoStudyColumns, matches, fuzzy);

            var sql = $@"
                SELECT 
                    s.*,
                    p.PatientName,
                    p.PatientSex,
                    p.PatientBirthDate,
                    COUNT(DISTINCT ser.SeriesInstanceUid) as NumberOfStudyRelatedSeries,
                    COUNT(DISTINCT i.SopInstanceUid) as NumberOfStudyRelatedInstances
                FROM Studies s
                LEFT JOIN Patients p ON s.PatientId = p.PatientId
                LEFT JOIN Series ser ON s.StudyInstanceUid = ser.StudyInstanceUid
                LEFT JOIN Instances i ON ser.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE 1=1 {whereSql}
                GROUP BY 
                    s.StudyInstanceUid, s.PatientId, s.StudyDate, s.StudyTime, s.StudyDescription,
                    s.AccessionNumber, s.Modality, s.InstitutionName, s.Remark, s.CreateTime,
                    p.PatientName, p.PatientSex, p.PatientBirthDate
                ORDER BY s.CreateTime DESC";

            if (offset.HasValue && limit.HasValue)
            {
                sql += " LIMIT @QidoLimit OFFSET @QidoOffset";
                parameters.Add("@QidoLimit", limit.Value);
                parameters.Add("@QidoOffset", offset.Value);
            }

            var result = connection.Query<Study>(sql, parameters).ToList();
            LogInformation("QIDO Study查询完成 - 返回记录数: {Count}", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "QIDO Study查询失败");
            if (throwOnError) throw;
            return [];
        }
    }

    public List<Series> QidoQuerySeries(string studyInstanceUid, IReadOnlyDictionary<DicomTag, IReadOnlyList<string>> matches, bool fuzzy, int? offset, int? limit, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var (whereSql, parameters) = BuildQidoWhere(QidoSeriesColumns, matches, fuzzy);

            var sql = $@"
                SELECT se.*, COUNT(i.SopInstanceUid) as NumberOfInstances
                FROM Series se
                LEFT JOIN Instances i ON se.SeriesInstanceUid = i.SeriesInstanceUid
                WHERE se.StudyInstanceUid = @QidoStudyInstanceUid {whereSql}
                GROUP BY se.SeriesInstanceUid, se.StudyInstanceUid, se.Modality, se.SeriesNumber,
                         se.SeriesDescription, se.SliceThickness, se.SeriesDate, se.CreateTime
                ORDER BY CAST(se.SeriesNumber as INTEGER)";

            parameters.Add("@QidoStudyInstanceUid", studyInstanceUid);
            if (offset.HasValue && limit.HasValue)
            {
                sql += " LIMIT @QidoLimit OFFSET @QidoOffset";
                parameters.Add("@QidoLimit", limit.Value);
                parameters.Add("@QidoOffset", offset.Value);
            }

            var result = connection.Query<Series>(sql, parameters).ToList();
            LogInformation("QIDO Series查询完成 - Study: {StudyInstanceUid}, 返回记录数: {Count}", studyInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "QIDO Series查询失败 - Study: {StudyInstanceUid}", studyInstanceUid);
            if (throwOnError) throw;
            return [];
        }
    }

    public List<Instance> QidoQueryInstances(string studyInstanceUid, string seriesInstanceUid, IReadOnlyDictionary<DicomTag, IReadOnlyList<string>> matches, bool fuzzy, int? offset, int? limit, bool throwOnError = false)
    {
        try
        {
            using var connection = CreateConnection();
            var (whereSql, parameters) = BuildQidoWhere(QidoInstanceColumns, matches, fuzzy);

            var sql = $@"
                SELECT i.*, se.StudyInstanceUid, se.Modality
                FROM Instances i
                INNER JOIN Series se ON i.SeriesInstanceUid = se.SeriesInstanceUid
                WHERE se.StudyInstanceUid = @QidoStudyInstanceUid 
                  AND i.SeriesInstanceUid = @QidoSeriesInstanceUid {whereSql}
                ORDER BY CAST(i.InstanceNumber as INTEGER)";

            parameters.Add("@QidoStudyInstanceUid", studyInstanceUid);
            parameters.Add("@QidoSeriesInstanceUid", seriesInstanceUid);
            if (offset.HasValue && limit.HasValue)
            {
                sql += " LIMIT @QidoLimit OFFSET @QidoOffset";
                parameters.Add("@QidoLimit", limit.Value);
                parameters.Add("@QidoOffset", offset.Value);
            }

            var result = connection.Query<Instance>(sql, parameters).ToList();
            LogInformation("QIDO Instance查询完成 - Study: {StudyInstanceUid}, Series: {SeriesInstanceUid}, 返回记录数: {Count}",
                studyInstanceUid, seriesInstanceUid, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            LogError(ex, "QIDO Instance查询失败 - Study: {StudyInstanceUid}, Series: {SeriesInstanceUid}", studyInstanceUid, seriesInstanceUid);
            if (throwOnError) throw;
            return [];
        }
    }

    /// <summary>将 QIDO 请求中的 DICOM 标签映射到数据库列并构造 SQL 过滤条件。</summary>
    private static readonly Dictionary<DicomTag, string> QidoStudyColumns = new()
    {
        [DicomTag.PatientID] = "p.PatientId",
        [DicomTag.PatientName] = "p.PatientName",
        [DicomTag.PatientBirthDate] = "p.PatientBirthDate",
        [DicomTag.PatientSex] = "p.PatientSex",
        [DicomTag.StudyInstanceUID] = "s.StudyInstanceUid",
        [DicomTag.StudyDate] = "s.StudyDate",
        [DicomTag.StudyTime] = "s.StudyTime",
        [DicomTag.AccessionNumber] = "s.AccessionNumber",
        [DicomTag.StudyDescription] = "s.StudyDescription",
        [DicomTag.ModalitiesInStudy] = "s.Modality",
        [DicomTag.Modality] = "s.Modality",
        [DicomTag.InstitutionName] = "s.InstitutionName"
    };

    private static readonly Dictionary<DicomTag, string> QidoSeriesColumns = new()
    {
        [DicomTag.SeriesInstanceUID] = "se.SeriesInstanceUid",
        [DicomTag.StudyInstanceUID] = "se.StudyInstanceUid",
        [DicomTag.Modality] = "se.Modality",
        [DicomTag.SeriesNumber] = "se.SeriesNumber",
        [DicomTag.SeriesDescription] = "se.SeriesDescription",
        [DicomTag.SliceThickness] = "se.SliceThickness",
        [DicomTag.SeriesDate] = "se.SeriesDate"
    };

    private static readonly Dictionary<DicomTag, string> QidoInstanceColumns = new()
    {
        [DicomTag.SOPInstanceUID] = "i.SopInstanceUid",
        [DicomTag.SOPClassUID] = "i.SopClassUid",
        [DicomTag.SeriesInstanceUID] = "i.SeriesInstanceUid",
        [DicomTag.InstanceNumber] = "i.InstanceNumber",
        [DicomTag.Rows] = "i.Rows",
        [DicomTag.Columns] = "i.Columns",
        [DicomTag.BitsAllocated] = "i.BitsAllocated",
        [DicomTag.BitsStored] = "i.BitsStored",
        [DicomTag.HighBit] = "i.HighBit",
        [DicomTag.PixelRepresentation] = "i.PixelRepresentation",
        [DicomTag.SamplesPerPixel] = "i.SamplesPerPixel",
        [DicomTag.PhotometricInterpretation] = "i.PhotometricInterpretation",
        [DicomTag.PixelSpacing] = "i.PixelSpacing",
        [DicomTag.ImageOrientationPatient] = "i.ImageOrientationPatient",
        [DicomTag.ImagePositionPatient] = "i.ImagePositionPatient",
        [DicomTag.FrameOfReferenceUID] = "i.FrameOfReferenceUID",
        [DicomTag.ImageType] = "i.ImageType",
        [DicomTag.WindowCenter] = "i.WindowCenter",
        [DicomTag.WindowWidth] = "i.WindowWidth"
    };

    /// <summary>将 QIDO 请求中的 DICOM 标签映射到数据库列并构造 SQL 过滤条件。</summary>
    private static (string WhereSql, DynamicParameters Parameters) BuildQidoWhere(
        Dictionary<DicomTag, string> columnMap,
        IReadOnlyDictionary<DicomTag, IReadOnlyList<string>> matches,
        bool fuzzy)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();
        var index = 0;

        foreach (var kv in matches)
        {
            if (!columnMap.TryGetValue(kv.Key, out var column))
            {
                continue;
            }

            foreach (var rawValue in kv.Value)
            {
                if (string.IsNullOrEmpty(rawValue)) continue;

                // 日期范围支持 "开始-结束"、"开始-" 与 "-结束"
                if (kv.Key == DicomTag.StudyDate && rawValue.Contains('-'))
                {
                    var parts = rawValue.Split('-');
                    var start = parts.Length > 0 ? parts[0].Trim() : "";
                    var end = parts.Length > 1 ? parts[1].Trim() : "";
                    if (start.Length > 0)
                    {
                        var p = $"@QidoP{index++}";
                        clauses.Add($"{column} >= {p}");
                        parameters.Add(p, start);
                    }
                    if (end.Length > 0)
                    {
                        var p = $"@QidoP{index++}";
                        clauses.Add($"{column} <= {p}");
                        parameters.Add(p, end);
                    }
                    continue;
                }

                var paramName = $"@QidoP{index++}";
                // 模糊匹配或含通配符时做子串匹配（DICOM 通配符 * 映射为 SQL %）；否则严格匹配
                var likeValue = rawValue.Replace("*", "%");
                if (fuzzy || likeValue.Contains('%'))
                {
                    clauses.Add($"{column} LIKE {paramName} ESCAPE '\\'");
                    var escaped = likeValue.Replace("\\", "\\\\").Replace("%", $"%");
                    parameters.Add(paramName, $"%{escaped.Trim('%')}%");
                }
                else
                {
                    clauses.Add($"{column} = {paramName}");
                    parameters.Add(paramName, likeValue);
                }
            }
        }

        var whereSql = clauses.Count > 0 ? " AND " + string.Join(" AND ", clauses) : string.Empty;
        return (whereSql, parameters);
    }
}