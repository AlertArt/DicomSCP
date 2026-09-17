using Dapper;
using DicomSCP.Models;
using Microsoft.Extensions.Configuration;

namespace DicomSCP.Repository;

/// <summary>
/// 执行程序步骤（MPPS）持久化仓储。
/// </summary>
public sealed class MppsRepository(IConfiguration configuration)
    : BaseRepository(configuration.GetConnectionString("DicomDb") ?? throw new ArgumentException("Missing DicomDb connection string"))
{
    public async Task<bool> InsertOrUpdateAsync(MppsRecord record)
    {
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            record.UpdateTime = DateTime.Now;

            await connection.ExecuteAsync(@"
                INSERT INTO MPPS (
                    MppsId, PerformedProcedureStepId, PerformedProcedureStepStatus,
                    PerformedProcedureStepStartDate, PerformedProcedureStepStartTime,
                    PerformedProcedureStepEndDate, PerformedProcedureStepEndTime,
                    PerformedProcedureStepDescription, PerformedProcedureTypeDescription,
                    PerformedStationAeTitle, PerformedStationName, PerformedLocation,
                    PerformedProcedureStepDiscontinuationReason,
                    Modality, StudyInstanceUid, AccessionNumber,
                    PatientName, PatientId, PatientBirthDate, PatientSex,
                    CallingAE, CreateTime, UpdateTime
                ) VALUES (
                    @MppsId, @PerformedProcedureStepId, @PerformedProcedureStepStatus,
                    @PerformedProcedureStepStartDate, @PerformedProcedureStepStartTime,
                    @PerformedProcedureStepEndDate, @PerformedProcedureStepEndTime,
                    @PerformedProcedureStepDescription, @PerformedProcedureTypeDescription,
                    @PerformedStationAeTitle, @PerformedStationName, @PerformedLocation,
                    @PerformedProcedureStepDiscontinuationReason,
                    @Modality, @StudyInstanceUid, @AccessionNumber,
                    @PatientName, @PatientId, @PatientBirthDate, @PatientSex,
                    @CallingAE, @CreateTime, @UpdateTime
                )
                ON CONFLICT(MppsId) DO UPDATE SET
                    PerformedProcedureStepId = @PerformedProcedureStepId,
                    PerformedProcedureStepStatus = @PerformedProcedureStepStatus,
                    PerformedProcedureStepStartDate = @PerformedProcedureStepStartDate,
                    PerformedProcedureStepStartTime = @PerformedProcedureStepStartTime,
                    PerformedProcedureStepEndDate = @PerformedProcedureStepEndDate,
                    PerformedProcedureStepEndTime = @PerformedProcedureStepEndTime,
                    PerformedProcedureStepDescription = @PerformedProcedureStepDescription,
                    PerformedProcedureTypeDescription = @PerformedProcedureTypeDescription,
                    PerformedStationAeTitle = @PerformedStationAeTitle,
                    PerformedStationName = @PerformedStationName,
                    PerformedLocation = @PerformedLocation,
                    PerformedProcedureStepDiscontinuationReason = @PerformedProcedureStepDiscontinuationReason,
                    Modality = @Modality,
                    StudyInstanceUid = @StudyInstanceUid,
                    AccessionNumber = @AccessionNumber,
                    PatientName = @PatientName,
                    PatientId = @PatientId,
                    PatientBirthDate = @PatientBirthDate,
                    PatientSex = @PatientSex,
                    CallingAE = @CallingAE,
                    UpdateTime = @UpdateTime", record, transaction);

            await connection.ExecuteAsync("DELETE FROM MPPSSeries WHERE MppsId = @MppsId", new { record.MppsId }, transaction);
            foreach (var series in record.Series ?? [])
            {
                series.MppsId = record.MppsId;
                await connection.ExecuteAsync(@"
                    INSERT INTO MPPSSeries (
                        MppsId, SeriesInstanceUid, Modality, SeriesDescription,
                        ProtocolName, PerformingPhysicianName, OperatorName, ReferencedSopUids
                    ) VALUES (
                        @MppsId, @SeriesInstanceUid, @Modality, @SeriesDescription,
                        @ProtocolName, @PerformingPhysicianName, @OperatorName, @ReferencedSopUids
                    )", series, transaction);
            }

            await transaction.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            LogError(ex, "插入或更新 MPPS 失败 - MppsId: {MppsId}", record.MppsId);
            throw;
        }
    }

    public async Task<MppsRecord?> GetAsync(string mppsId)
    {
        try
        {
            using var connection = CreateConnection();
            var record = await connection.QueryFirstOrDefaultAsync<MppsRecord>(
                "SELECT * FROM MPPS WHERE MppsId = @MppsId", new { MppsId = mppsId });
            if (record != null)
            {
                record.Series = (await connection.QueryAsync<MppsSeriesRecord>(
                    "SELECT * FROM MPPSSeries WHERE MppsId = @MppsId ORDER BY Id", new { MppsId = mppsId })).ToList();
            }
            return record;
        }
        catch (Exception ex)
        {
            LogError(ex, "查询 MPPS 失败 - MppsId: {MppsId}", mppsId);
            throw;
        }
    }

    public async Task<List<MppsRecord>> GetRecentAsync(string? status = null, int limit = 50)
    {
        try
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM MPPS WHERE 1=1";
            var parameters = new DynamicParameters();
            if (!string.IsNullOrEmpty(status))
            {
                sql += " AND PerformedProcedureStepStatus = @Status";
                parameters.Add("@Status", status);
            }
            sql += " ORDER BY UpdateTime DESC LIMIT @Limit";
            parameters.Add("@Limit", limit);
            return (await connection.QueryAsync<MppsRecord>(sql, parameters)).ToList();
        }
        catch (Exception ex)
        {
            LogError(ex, "查询 MPPS 列表失败");
            throw;
        }
    }
}