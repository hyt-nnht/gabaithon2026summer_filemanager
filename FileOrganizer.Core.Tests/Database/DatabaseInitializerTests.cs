using FileOrganizer.Core.Database;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Tests.Database;

/// <summary>
/// AI_IMPLEMENTATION_GUIDE.md §2のDDL適用とWALモード有効化を検証する。
/// 対象: <see cref="DatabaseInitializer"/>。
/// </summary>
public class DatabaseInitializerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "DatabaseInitializer", Guid.NewGuid().ToString("N"));

    public DatabaseInitializerTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの後始末失敗は無視。
        }
    }

    private string NewDbPath(string name = "test.db") => Path.Combine(_workDir, name);

    private static async Task<bool> ObjectExistsAsync(SqliteConnection connection, string type, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = @type AND name = @name;";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        long count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    [Fact]
    public async Task InitializeAsync_operation_historyテーブルと両インデックスを作成する()
    {
        string dbPath = NewDbPath();
        var initializer = new DatabaseInitializer(DatabaseInitializer.BuildConnectionString(dbPath));

        await initializer.InitializeAsync();

        await using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(dbPath));
        await connection.OpenAsync();

        Assert.True(await ObjectExistsAsync(connection, "table", "operation_history"));
        Assert.True(await ObjectExistsAsync(connection, "index", "idx_history_state"));
        Assert.True(await ObjectExistsAsync(connection, "index", "idx_history_created_at"));
    }

    [Fact]
    public async Task InitializeAsync_WALジャーナルモードを有効化する()
    {
        string dbPath = NewDbPath();
        var initializer = new DatabaseInitializer(DatabaseInitializer.BuildConnectionString(dbPath));

        await initializer.InitializeAsync();

        await using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(dbPath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        string mode = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task InitializeAsync_保存先ディレクトリが未作成でも自動作成してDBファイルを生成する()
    {
        string dbPath = Path.Combine(_workDir, "nested", "sub", "organizer.db");
        var initializer = new DatabaseInitializer(DatabaseInitializer.BuildConnectionString(dbPath));

        await initializer.InitializeAsync();

        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task InitializeAsync_複数回実行しても例外を投げず冪等である()
    {
        string dbPath = NewDbPath();
        var initializer = new DatabaseInitializer(DatabaseInitializer.BuildConnectionString(dbPath));

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(dbPath));
        await connection.OpenAsync();
        Assert.True(await ObjectExistsAsync(connection, "table", "operation_history"));
    }

    [Fact]
    public void BuildConnectionString_DataSourceにファイルパスが設定される()
    {
        string dbPath = NewDbPath("custom.db");
        string connectionString = DatabaseInitializer.BuildConnectionString(dbPath);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        Assert.Equal(dbPath, builder.DataSource);
    }

    [Fact]
    public void GetDefaultDatabaseFilePath_LocalAppData配下のFileOrganizerフォルダを指す()
    {
        string path = DatabaseInitializer.GetDefaultDatabaseFilePath();

        Assert.Contains("FileOrganizer", path);
        Assert.EndsWith("organizer.db", path);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path);
    }
}
