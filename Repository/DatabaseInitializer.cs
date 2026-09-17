using Dapper;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

/// <summary>
/// 数据库初始化器：负责建表、初始化数据和结构迁移。
/// </summary>
public static class DatabaseInitializer
{
    public static async Task<bool> InitializeAsync(string connectionString)
    {
        // 确保数据库目录存在
        var dbPath = Path.GetDirectoryName(connectionString.Replace("Data Source=", "").Trim());
        if (!string.IsNullOrEmpty(dbPath))
        {
            Directory.CreateDirectory(dbPath);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // 检查是否已存在表，用于判断是否首次初始化
        var tableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Studies'");

        await connection.ExecuteAsync(DatabaseSchemaSql.CreatePatientsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateStudiesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateSeriesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateInstancesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateWorklistTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateUsersTable, transaction: transaction);

        // 初始化默认管理员：PBKDF2 + 每次安装独立随机盐（替代源码硬编码哈希）。
        // 已存在则不覆盖，保留用户修改后的口令（与原 INSERT OR IGNORE 语义一致）
        var adminExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = 'admin'",
            transaction: transaction);
        if (adminExists == 0)
        {
            await connection.ExecuteAsync(
                "INSERT INTO Users (Username, Password) VALUES ('admin', @Password)",
                new { Password = PasswordHasher.Hash("admin", PasswordHasher.DefaultIterations) },
                transaction: transaction);
        }

        await connection.ExecuteAsync(DatabaseSchemaSql.CreatePrintJobsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateStorageCommitmentsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateMppsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateMppsSeriesTable, transaction: transaction);

        // 在建表完成后执行字段升级迁移
        await DatabaseSchemaMigrator.MigrateAsync(connection, transaction);

        await transaction.CommitAsync();

        return tableExists == 0;
    }
}
