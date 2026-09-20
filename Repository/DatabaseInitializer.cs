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
        var dbPath = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource.Trim());
        if (!string.IsNullOrEmpty(dbPath))
        {
            Directory.CreateDirectory(dbPath);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // WAL 是数据库文件级属性，只需设置一次即持久生效：
        // 允许读写并发（读不阻塞写、写不阻塞读），显著降低批量入库时的 SQLITE_BUSY。
        // journal_mode 不能在事务内修改，因此必须在 BeginTransaction 之前执行。
        await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");
        await connection.ExecuteAsync("PRAGMA synchronous=NORMAL;");
        await connection.ExecuteAsync("PRAGMA busy_timeout=30000;");

        await using var transaction = await connection.BeginTransactionAsync();

        // 检查是否已存在表，用于判断是否首次初始化
        var tableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Studies'");

        await connection.ExecuteAsync(DatabaseSchemaSql.CreatePatientsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateStudiesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateSeriesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateInstancesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateSrReferencedInstancesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateWorklistTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateUsersTable, transaction: transaction);

        // 初始化默认管理员：PBKDF2 + 每次安装独立随机盐（替代源码硬编码哈希）。
        // 已存在则不覆盖，保留用户修改后的口令（与原 INSERT OR IGNORE 语义一致）。
        // 新装默认口令标记为必须改密，避免长期使用 admin/admin。
        var adminExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Username = 'admin'",
            transaction: transaction);
        if (adminExists == 0)
        {
            await connection.ExecuteAsync(
                "INSERT INTO Users (Username, Password, MustChangePassword) VALUES ('admin', @Password, 1)",
                new { Password = PasswordHasher.Hash("admin", PasswordHasher.DefaultIterations) },
                transaction: transaction);
        }

        await connection.ExecuteAsync(DatabaseSchemaSql.CreatePrintJobsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateStorageCommitmentsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateMppsTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateMppsSeriesTable, transaction: transaction);
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateUpsWorkItemsTable, transaction: transaction);

        // 为新建库与历史库补齐查询索引（幂等）
        await connection.ExecuteAsync(DatabaseSchemaSql.CreateIndexes, transaction: transaction);

        // 在建表完成后执行字段升级迁移
        await DatabaseSchemaMigrator.MigrateAsync(connection, transaction);

        await transaction.CommitAsync();

        return tableExists == 0;
    }
}
