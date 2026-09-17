using Dapper;
using DicomSCP.Models;
using Microsoft.Extensions.Configuration;

namespace DicomSCP.Repository;

/// <summary>
/// 统一程序步骤（UPS）工作项持久化仓储。
/// </summary>
public sealed class UpsRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    public async Task<bool> InsertOrUpdateAsync(UpsWorkItem item)
    {
        try
        {
            using var connection = CreateConnection();
            item.UpdateTime = DateTime.Now;
            await connection.ExecuteAsync(@"
                INSERT INTO UPSWorkItems (
                    SopInstanceUid, WorkItemLabel, ProcedureStepState, Priority,
                    Modality, ScheduledAeTitle, ScheduledStartDate, ScheduledStartTime,
                    PatientName, PatientId, AccessionNumber, RequestedProcedureId,
                    CalledAeTitle, CallingAe, DatasetJson, CreateTime, UpdateTime
                ) VALUES (
                    @SopInstanceUid, @WorkItemLabel, @ProcedureStepState, @Priority,
                    @Modality, @ScheduledAeTitle, @ScheduledStartDate, @ScheduledStartTime,
                    @PatientName, @PatientId, @AccessionNumber, @RequestedProcedureId,
                    @CalledAeTitle, @CallingAe, @DatasetJson, @CreateTime, @UpdateTime
                )
                ON CONFLICT(SopInstanceUid) DO UPDATE SET
                    WorkItemLabel = @WorkItemLabel,
                    ProcedureStepState = @ProcedureStepState,
                    Priority = @Priority,
                    Modality = @Modality,
                    ScheduledAeTitle = @ScheduledAeTitle,
                    ScheduledStartDate = @ScheduledStartDate,
                    ScheduledStartTime = @ScheduledStartTime,
                    PatientName = @PatientName,
                    PatientId = @PatientId,
                    AccessionNumber = @AccessionNumber,
                    RequestedProcedureId = @RequestedProcedureId,
                    CalledAeTitle = @CalledAeTitle,
                    CallingAe = @CallingAe,
                    DatasetJson = @DatasetJson,
                    UpdateTime = @UpdateTime", item);
            return true;
        }
        catch (Exception ex)
        {
            LogError(ex, "插入或更新 UPS 工作项失败 - SopInstanceUid: {SopInstanceUid}", item.SopInstanceUid);
            throw;
        }
    }

    public async Task<UpsWorkItem?> GetAsync(string sopInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<UpsWorkItem>(
                "SELECT * FROM UPSWorkItems WHERE SopInstanceUid = @SopInstanceUid",
                new { SopInstanceUid = sopInstanceUid });
        }
        catch (Exception ex)
        {
            LogError(ex, "查询 UPS 工作项失败 - SopInstanceUid: {SopInstanceUid}", sopInstanceUid);
            throw;
        }
    }

    public async Task<List<UpsWorkItem>> QueryAsync(string? status = null, string? modality = null,
        string? station = null, string? patientId = null, int limit = 100)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM UPSWorkItems WHERE 1=1";
            var parameters = new DynamicParameters();
            if (!string.IsNullOrEmpty(status))
            {
                sql += " AND ProcedureStepState = @Status";
                parameters.Add("@Status", status);
            }
            if (!string.IsNullOrEmpty(modality))
            {
                sql += " AND Modality = @Modality";
                parameters.Add("@Modality", modality);
            }
            if (!string.IsNullOrEmpty(station))
            {
                sql += " AND ScheduledAeTitle = @Station";
                parameters.Add("@Station", station);
            }
            if (!string.IsNullOrEmpty(patientId))
            {
                sql += " AND PatientId = @PatientId";
                parameters.Add("@PatientId", patientId);
            }
            sql += " ORDER BY CreateTime DESC LIMIT @Limit";
            parameters.Add("@Limit", limit);
            return (await connection.QueryAsync<UpsWorkItem>(sql, parameters)).ToList();
        }
        catch (Exception ex)
        {
            LogError(ex, "查询 UPS 工作项列表失败");
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string sopInstanceUid)
    {
        try
        {
            using var connection = CreateConnection();
            return await connection.ExecuteAsync(
                "DELETE FROM UPSWorkItems WHERE SopInstanceUid = @SopInstanceUid",
                new { SopInstanceUid = sopInstanceUid }) > 0;
        }
        catch (Exception ex)
        {
            LogError(ex, "删除 UPS 工作项失败 - SopInstanceUid: {SopInstanceUid}", sopInstanceUid);
            throw;
        }
    }
}