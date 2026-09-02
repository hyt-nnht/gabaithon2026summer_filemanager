using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Database;

/// <summary>
/// AI_IMPLEMENTATION_GUIDE.md §2のDDL（<c>operation_history</c>テーブル・
/// <c>idx_history_state</c>・<c>idx_history_created_at</c>）をMicrosoft.Data.Sqliteで適用し、
/// WALジャーナルモードを有効化する初期化専用クラス。
/// 仕様書§3.3「2フェーズ状態遷移」の永続化基盤、および§7.2-4「長時間常駐安定性
/// （SQLite WALチェックポイント）」の前提となるスキーマを提供する。
/// </summary>
/// <remarks>
/// <see cref="InitializeAsync"/>はDB未作成時のスキーマ作成に加え、既存DBに対しても
/// <c>IF NOT EXISTS</c>句により冪等に再実行できるため、アプリ起動のたびに呼び出してよい。
/// </remarks>
public sealed class DatabaseInitializer
{
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS operation_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            operation_id TEXT NOT NULL UNIQUE,
            op_type TEXT NOT NULL,
            source_path TEXT NOT NULL,
            destination_path TEXT,
            file_size_bytes INTEGER NOT NULL,
            file_last_modified_utc TEXT NOT NULL,
            lightweight_hash TEXT NOT NULL,
            state TEXT NOT NULL,
            error_message TEXT,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );
        """;

    private const string CreateStateIndexSql =
        "CREATE INDEX IF NOT EXISTS idx_history_state ON operation_history(state);";

    private const string CreateCreatedAtIndexSql =
        "CREATE INDEX IF NOT EXISTS idx_history_created_at ON operation_history(created_at_utc DESC);";

    private readonly string _connectionString;

    /// <param name="connectionString">
    /// Microsoft.Data.Sqlite接続文字列（例: <see cref="BuildConnectionString"/>で生成したもの）。
    /// </param>
    public DatabaseInitializer(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("接続文字列を空にすることはできません。", nameof(connectionString));

        _connectionString = connectionString;
    }

    /// <summary>DBファイルパスからMicrosoft.Data.Sqlite用の接続文字列を組み立てる。</summary>
    public static string BuildConnectionString(string databaseFilePath)
        => new SqliteConnectionStringBuilder { DataSource = databaseFilePath }.ToString();

    /// <summary>
    /// アプリ既定のDB保存先（<c>%LocalAppData%\FileOrganizer\organizer.db</c>）を返す。
    /// 呼び出し側が明示的な保存先を持たない場合（本番起動時など）に使用する。
    /// </summary>
    public static string GetDefaultDatabaseFilePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileOrganizer");
        return Path.Combine(dir, "organizer.db");
    }

    /// <summary>
    /// DBファイルの保存先ディレクトリを作成し、WALモードを有効化した上でスキーマ（テーブル・インデックス）を
    /// 作成する。既に作成済みの場合は何もしない（冪等）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        EnsureDatabaseDirectoryExists();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // WALモード有効化（仕様書§7.2-4、長時間常駐時のDB肥大化防止の前提）。
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, CreateTableSql, ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, CreateStateIndexSql, ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, CreateCreatedAtIndexSql, ct).ConfigureAwait(false);
    }

    private void EnsureDatabaseDirectoryExists()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        string dataSource = builder.DataSource;

        // ":memory:" や共有インメモリDB等、実ファイルを伴わない接続文字列は対象外。
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return;

        string? dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
