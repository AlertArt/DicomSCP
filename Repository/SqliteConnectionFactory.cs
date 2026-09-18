using System.Data;
using Microsoft.Data.Sqlite;

namespace DicomSCP.Repository;

/// <summary>
/// 统一创建 SQLite 连接。
/// WAL 模式是数据库文件级属性，在 <see cref="DatabaseInitializer"/> 中一次性开启后对后续连接持久生效；
/// busy_timeout 是连接级 PRAGMA，Microsoft.Data.Sqlite 不支持通过连接串设置，
/// 因此在此对每个新连接应用：写入遇到短暂锁竞争时等待而非立即抛出 SQLITE_BUSY。
/// </summary>
public static class SqliteConnectionFactory
{
    /// <summary>遇锁等待时长（毫秒）。与 Microsoft.Data.Sqlite 的 busy/locked 自动重试叠加。</summary>
    public const int BusyTimeoutMs = 30_000;

    public static SqliteConnection Create(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
            {
                ApplyPragmas(connection);
            }
        };
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
            command.ExecuteNonQuery();
        }
        catch
        {
            // PRAGMA 应用失败不应阻断业务查询
        }
    }
}
