using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;

namespace DicomSCP.Repository;

/// <summary>
/// 数据库结构迁移器：集中管理历史数据库字段升级。
/// </summary>
public static class DatabaseSchemaMigrator
{
    /// <summary>
    /// 执行所有已定义的数据库结构迁移。
    /// </summary>
    public static async Task MigrateAsync(SqliteConnection connection, IDbTransaction? transaction = null)
    {
        await EnsureStudyRemarkColumnAsync(connection, transaction);
        await EnsureMustChangePasswordColumnAsync(connection, transaction);
        await EnsureSrInstanceColumnsAsync(connection, transaction);
    }

    private static async Task EnsureStudyRemarkColumnAsync(SqliteConnection connection, IDbTransaction? transaction)
    {
        var studyRemarkColumnExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM pragma_table_info('Studies') WHERE name = 'Remark'",
            transaction: transaction);

        if (studyRemarkColumnExists == 0)
        {
            await connection.ExecuteAsync("ALTER TABLE Studies ADD COLUMN Remark TEXT", transaction: transaction);
        }
    }

    /// <summary>
    /// 为历史库补齐 Users.MustChangePassword。补列后若管理员仍在使用默认口令，
    /// 则标记为必须改密（新装库在种子阶段已直接写入 1）。
    /// </summary>
    private static async Task EnsureMustChangePasswordColumnAsync(SqliteConnection connection, IDbTransaction? transaction)
    {
        var columnExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'MustChangePassword'",
            transaction: transaction);

        if (columnExists > 0)
        {
            return;
        }

        await connection.ExecuteAsync(
            "ALTER TABLE Users ADD COLUMN MustChangePassword INTEGER NOT NULL DEFAULT 0",
            transaction: transaction);

        var adminPassword = await connection.ExecuteScalarAsync<string>(
            "SELECT Password FROM Users WHERE Username = 'admin'",
            transaction: transaction);

        if (adminPassword != null && PasswordHasher.Verify("admin", adminPassword, out _))
        {
            await connection.ExecuteAsync(
                "UPDATE Users SET MustChangePassword = 1 WHERE Username = 'admin'",
                transaction: transaction);
        }
    }

    /// <summary>为历史库补齐 Instances 表的结构化报告(SR)字段。</summary>
    private static async Task EnsureSrInstanceColumnsAsync(SqliteConnection connection, IDbTransaction? transaction)
    {
        var srColumns = new (string Name, string Type)[]
        {
            ("DocumentTitle", "TEXT"),
            ("CompletionFlag", "TEXT"),
            ("VerificationFlag", "TEXT"),
            ("ConceptCodeValue", "TEXT"),
            ("ConceptCodingSchemeDesignator", "TEXT"),
            ("ConceptCodeMeaning", "TEXT"),
            ("VerificationDateTime", "TEXT"),
            ("ContentDate", "TEXT"),
            ("ContentTime", "TEXT"),
        };

        foreach (var (name, type) in srColumns)
        {
            var exists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pragma_table_info('Instances') WHERE name = @Name",
                new { Name = name },
                transaction: transaction);

            if (exists == 0)
            {
                await connection.ExecuteAsync(
                    $"ALTER TABLE Instances ADD COLUMN {name} {type}",
                    transaction: transaction);
            }
        }
    }
}
