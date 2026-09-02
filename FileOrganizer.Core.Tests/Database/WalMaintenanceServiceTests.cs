using FileOrganizer.Core.Database;
using FileOrganizer.Shared.Models;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Tests.Database;

/// <summary>
/// 仕様書§7.2-4「長時間常駐安定性」（SQLite WALチェックポイントによるDBファイル肥大化防止）の
/// 受け入れ基準を検証する。対象: <see cref="WalMaintenanceService"/>。
/// </summary>
public class WalMaintenanceServiceTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "WalMaintenanceService", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _connectionString;

    public WalMaintenanceServiceTests()
    {
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "history.db");
        _connectionString = DatabaseInitializer.BuildConnectionString(_dbPath);
        new DatabaseInitializer(_connectionString).InitializeAsync().GetAwaiter().GetResult();
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "条件がタイムアウト内に満たされませんでした。");
    }

    private long WalFileSize()
    {
        string walPath = _dbPath + "-wal";
        return File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
    }

    private async Task GrowWalAsync(int recordCount = 300)
    {
        var repository = new SqliteHistoryRepository(_connectionString);
        for (int i = 0; i < recordCount; i++)
        {
            await repository.InsertAsync(new HistoryRecord
            {
                OperationId = Guid.NewGuid().ToString("N"),
                OpType = OperationType.Move,
                SourcePath = $@"C:\watch\file-{i}.pdf",
                DestinationPath = $@"D:\organized\file-{i}.pdf",
                FileSizeBytes = 1024,
                FileLastModifiedUtc = DateTime.UtcNow,
                LightweightHash = new string('A', 64),
                State = OperationState.Completed,
            });
        }
    }

    /// <summary>
    /// WALファイルを確実に非0バイトへ育てるため、単一の永続接続・単一トランザクションで
    /// 大量INSERTを行う（<see cref="SqliteHistoryRepository"/>のような呼び出しごとのopen/closeだと、
    /// プーリングされた接続が入れ替わるタイミング次第でSQLiteの自動チェックポイントが不定期に走り、
    /// WALサイズの検証が不安定になるため）。呼び出し元は返された接続を明示的に閉じること。
    /// </summary>
    private SqliteConnection GrowWalReliably(int recordCount = 2000)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation_history
                (operation_id, op_type, source_path, destination_path, file_size_bytes,
                 file_last_modified_utc, lightweight_hash, state, error_message, created_at_utc, updated_at_utc)
            VALUES
                (@op_id, 'Move', @src, @dst, 1024, @ts, @hash, 'Completed', NULL, @ts, @ts);
            """;
        var opIdParam = command.CreateParameter();
        opIdParam.ParameterName = "@op_id";
        command.Parameters.Add(opIdParam);
        var srcParam = command.CreateParameter();
        srcParam.ParameterName = "@src";
        command.Parameters.Add(srcParam);
        var dstParam = command.CreateParameter();
        dstParam.ParameterName = "@dst";
        command.Parameters.Add(dstParam);
        var tsParam = command.CreateParameter();
        tsParam.ParameterName = "@ts";
        command.Parameters.Add(tsParam);
        var hashParam = command.CreateParameter();
        hashParam.ParameterName = "@hash";
        command.Parameters.Add(hashParam);

        string ts = DateTime.UtcNow.ToString("O");
        string hash = new string('A', 64);
        for (int i = 0; i < recordCount; i++)
        {
            opIdParam.Value = Guid.NewGuid().ToString("N");
            srcParam.Value = $@"C:\watch\file-{i}.pdf";
            dstParam.Value = $@"D:\organized\file-{i}.pdf";
            tsParam.Value = ts;
            hashParam.Value = hash;
            command.ExecuteNonQuery();
        }
        transaction.Commit();

        return connection;
    }

    // --- RunCheckpointNowAsync: 実際のWALファイルへの効果 --------------------------------------

    [Fact]
    public async Task RunCheckpointNowAsync_WALファイルを切り詰める()
    {
        using var growConnection = GrowWalReliably();
        long walSizeBeforeCheckpoint = WalFileSize();
        Assert.True(walSizeBeforeCheckpoint > 0, "前提条件: チェックポイント前にWALファイルが育っていること。");

        using var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);
        var result = await service.RunCheckpointNowAsync();

        Assert.False(result.Busy);

        long walSizeAfterCheckpoint = WalFileSize();
        Assert.True(walSizeAfterCheckpoint < walSizeBeforeCheckpoint,
            $"チェックポイント後にWALファイルが縮小しているはず（前: {walSizeBeforeCheckpoint}バイト、後: {walSizeAfterCheckpoint}バイト）。");
        // TRUNCATEモードでは他に読み取り中の接続が無ければ0バイトまで切り詰められる。
        Assert.Equal(0, walSizeAfterCheckpoint);
    }

    [Fact]
    public async Task RunCheckpointNowAsync_結果がCheckpointCompletedイベントで通知される()
    {
        await GrowWalAsync(50);
        using var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);

        WalCheckpointResult? received = null;
        service.CheckpointCompleted += (_, e) => received = e;

        var result = await service.RunCheckpointNowAsync();

        Assert.NotNull(received);
        Assert.Same(result, received);
    }

    [Fact]
    public async Task RunCheckpointNowAsync_データを壊さずDBの内容はそのまま読み取れる()
    {
        await GrowWalAsync(50);
        using var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);
        await service.RunCheckpointNowAsync();

        var repository = new SqliteHistoryRepository(_connectionString);
        var recent = await repository.GetRecentAsync(1000);

        Assert.Equal(50, recent.Count);
    }

    [Fact]
    public async Task RunCheckpointNowAsync_育っていない状態で実行しても例外を投げない()
    {
        using var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);

        var result = await service.RunCheckpointNowAsync();

        Assert.False(result.Busy);
    }

    // --- 定期実行（バックグラウンドタイマー） --------------------------------------------------

    [Fact]
    public async Task 定期実行_バックグラウンドループが指定間隔で自動的にチェックポイントを実行する()
    {
        await GrowWalAsync(50);

        // 公開APIの間隔単位は「分」だが、内部向けTimeSpanコンストラクタ（テスト専用）を使い、
        // 実際のPeriodicTimerによるバックグラウンドループそのものが定期実行することを検証する
        // （分単位のまま待つのは非現実的なため）。
        using var service = new WalMaintenanceService(_connectionString, TimeSpan.FromMilliseconds(30));

        int fireCount = 0;
        service.CheckpointCompleted += (_, _) => Interlocked.Increment(ref fireCount);

        await WaitUntilAsync(() => Volatile.Read(ref fireCount) >= 3, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 手動実行_RunCheckpointNowAsyncを複数回呼ぶとその都度イベントが発火する()
    {
        await GrowWalAsync(50);
        using var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);

        int fireCount = 0;
        service.CheckpointCompleted += (_, _) => Interlocked.Increment(ref fireCount);

        await service.RunCheckpointNowAsync();
        await service.RunCheckpointNowAsync();

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public async Task Dispose_バックグラウンドループが停止し以降チェックポイントは発火しない()
    {
        await GrowWalAsync(50);
        var service = new WalMaintenanceService(_connectionString, TimeSpan.FromMilliseconds(20));

        int fireCount = 0;
        service.CheckpointCompleted += (_, _) => Interlocked.Increment(ref fireCount);

        await WaitUntilAsync(() => Volatile.Read(ref fireCount) >= 1, TimeSpan.FromSeconds(5));
        service.Dispose();
        int countAtDispose = Volatile.Read(ref fireCount);

        await Task.Delay(200); // ループが誤動作していないことを確認するための猶予
        Assert.Equal(countAtDispose, Volatile.Read(ref fireCount));
    }

    [Fact]
    public void Dispose_例外を投げずに停止できる()
    {
        var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);
        var exception = Record.Exception(() => service.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_複数回呼び出しても例外を投げない()
    {
        var service = new WalMaintenanceService(_connectionString, checkpointIntervalMinutes: 60);
        service.Dispose();
        var exception = Record.Exception(() => service.Dispose());
        Assert.Null(exception);
    }

    // --- コンストラクタ引数検証 ---------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_接続文字列が空の場合は例外を投げる(string connectionString)
    {
        Assert.Throws<ArgumentException>(() => new WalMaintenanceService(connectionString));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_間隔が0以下の場合は例外を投げる(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalMaintenanceService(_connectionString, minutes));
    }

    [Fact]
    public void DefaultCheckpointIntervalMinutes_AppSettingsの既定値60分と一致する()
    {
        Assert.Equal(60, WalMaintenanceService.DefaultCheckpointIntervalMinutes);
    }
}
