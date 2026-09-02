using FileOrganizer.Core.Database;
using FileOrganizer.Shared.Models;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Tests.Database;

/// <summary>
/// 仕様書§3.3「ファイル操作とSQLiteの障害復旧（2フェーズ状態遷移）」の受け入れ基準を検証する。
/// 対象: <see cref="SqliteHistoryRepository"/>（AI_IMPLEMENTATION_GUIDE.md §1.2/§2準拠）。
/// </summary>
public class SqliteHistoryRepositoryTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "SqliteHistoryRepository", Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly SqliteHistoryRepository _repository;

    public SqliteHistoryRepositoryTests()
    {
        Directory.CreateDirectory(_workDir);
        string dbPath = Path.Combine(_workDir, "history.db");
        _connectionString = DatabaseInitializer.BuildConnectionString(dbPath);

        // スキーマ作成はDatabaseInitializerの責務。テスト対象のRepositoryは作成済みDBに対して動作する。
        new DatabaseInitializer(_connectionString).InitializeAsync().GetAwaiter().GetResult();

        _repository = new SqliteHistoryRepository(_connectionString);
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

    private static HistoryRecord CreateSampleRecord(
        string? operationId = null,
        OperationType opType = OperationType.Move,
        OperationState state = OperationState.Planned,
        string sourcePath = @"C:\watch\sample.pdf",
        string? destinationPath = null)
    {
        var now = DateTime.UtcNow;
        return new HistoryRecord
        {
            OperationId = operationId ?? Guid.NewGuid().ToString("N"),
            OpType = opType,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            FileSizeBytes = 12345,
            FileLastModifiedUtc = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc),
            LightweightHash = "ABCDEF0123456789",
            State = state,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    // --- Insert / Get (Create・Read) ---------------------------------------------------

    [Fact]
    public async Task InsertAsync_採番されたIdを返しGetByIdAsyncで全項目を復元できる()
    {
        var record = CreateSampleRecord(destinationPath: @"D:\organized\sample.pdf");

        long id = await _repository.InsertAsync(record);

        Assert.True(id > 0);
        Assert.Equal(id, record.Id);

        var fetched = await _repository.GetByIdAsync(id);
        Assert.NotNull(fetched);
        Assert.Equal(record.OperationId, fetched!.OperationId);
        Assert.Equal(record.OpType, fetched.OpType);
        Assert.Equal(record.SourcePath, fetched.SourcePath);
        Assert.Equal(record.DestinationPath, fetched.DestinationPath);
        Assert.Equal(record.FileSizeBytes, fetched.FileSizeBytes);
        Assert.Equal(record.FileLastModifiedUtc, fetched.FileLastModifiedUtc);
        Assert.Equal(record.LightweightHash, fetched.LightweightHash);
        Assert.Equal(record.State, fetched.State);
        Assert.Null(fetched.ErrorMessage);
    }

    [Fact]
    public async Task InsertAsync_DestinationPathとErrorMessageがnullでも保存復元できる()
    {
        var record = CreateSampleRecord(destinationPath: null);

        long id = await _repository.InsertAsync(record);
        var fetched = await _repository.GetByIdAsync(id);

        Assert.NotNull(fetched);
        Assert.Null(fetched!.DestinationPath);
        Assert.Null(fetched.ErrorMessage);
    }

    [Fact]
    public async Task InsertAsync_OperationIdが重複する場合は一意制約違反になる()
    {
        string operationId = Guid.NewGuid().ToString("N");
        await _repository.InsertAsync(CreateSampleRecord(operationId));

        await Assert.ThrowsAsync<SqliteException>(
            () => _repository.InsertAsync(CreateSampleRecord(operationId)));
    }

    [Fact]
    public async Task GetByIdAsync_存在しないIdはnullを返す()
    {
        var result = await _repository.GetByIdAsync(999_999);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByOperationIdAsync_一致するレコードを取得できる()
    {
        var record = CreateSampleRecord();
        await _repository.InsertAsync(record);

        var fetched = await _repository.GetByOperationIdAsync(record.OperationId);

        Assert.NotNull(fetched);
        Assert.Equal(record.SourcePath, fetched!.SourcePath);
    }

    [Fact]
    public async Task GetByOperationIdAsync_一致しない場合はnullを返す()
    {
        var result = await _repository.GetByOperationIdAsync("does-not-exist");
        Assert.Null(result);
    }

    // --- GetRecordsByStateAsync ------------------------------------------------------

    [Fact]
    public async Task GetRecordsByStateAsync_指定した状態のレコードのみ取得できる()
    {
        var planned1 = CreateSampleRecord(state: OperationState.Planned);
        var planned2 = CreateSampleRecord(state: OperationState.Planned);
        var executing = CreateSampleRecord(state: OperationState.Executing);
        await _repository.InsertAsync(planned1);
        await _repository.InsertAsync(planned2);
        await _repository.InsertAsync(executing);

        var plannedResults = await _repository.GetRecordsByStateAsync(OperationState.Planned);

        Assert.Equal(2, plannedResults.Count);
        Assert.All(plannedResults, r => Assert.Equal(OperationState.Planned, r.State));
        Assert.Contains(plannedResults, r => r.OperationId == planned1.OperationId);
        Assert.Contains(plannedResults, r => r.OperationId == planned2.OperationId);
    }

    [Fact]
    public async Task GetRecordsByStateAsync_該当なしの場合は空リストを返す()
    {
        var results = await _repository.GetRecordsByStateAsync(OperationState.UndoFailed);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    // --- GetRecentAsync ----------------------------------------------------------------

    [Fact]
    public async Task GetRecentAsync_created_at_utc降順で指定件数取得できる()
    {
        var older = CreateSampleRecord();
        older.CreatedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var middle = CreateSampleRecord();
        middle.CreatedAtUtc = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        var newest = CreateSampleRecord();
        newest.CreatedAtUtc = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

        await _repository.InsertAsync(older);
        await _repository.InsertAsync(middle);
        await _repository.InsertAsync(newest);

        var recent = await _repository.GetRecentAsync(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal(newest.OperationId, recent[0].OperationId);
        Assert.Equal(middle.OperationId, recent[1].OperationId);
    }

    // --- UpdateStateAsync ---------------------------------------------------------------

    [Fact]
    public async Task UpdateStateAsync_存在しないIdはKeyNotFoundExceptionを投げる()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _repository.UpdateStateAsync(999_999, OperationState.Completed));
    }

    [Fact]
    public async Task UpdateStateAsync_エラーメッセージ省略時は既存のエラーがnullでクリアされる()
    {
        var record = CreateSampleRecord(state: OperationState.Executing);
        long id = await _repository.InsertAsync(record);
        await _repository.UpdateStateAsync(id, OperationState.Failed, "一時的なI/Oエラー");

        // Failed から再実行 → Executing へ戻す想定で、エラーメッセージを省略（null）して更新。
        await _repository.UpdateStateAsync(id, OperationState.Executing);

        var fetched = await _repository.GetByIdAsync(id);
        Assert.NotNull(fetched);
        Assert.Equal(OperationState.Executing, fetched!.State);
        Assert.Null(fetched.ErrorMessage);
    }

    [Fact]
    public async Task UpdateStateAsync_UpdatedAtUtcが更新のたびに進む()
    {
        var record = CreateSampleRecord();
        long id = await _repository.InsertAsync(record);
        var afterInsert = (await _repository.GetByIdAsync(id))!;

        await Task.Delay(20);
        await _repository.UpdateStateAsync(id, OperationState.Executing);
        var afterUpdate = (await _repository.GetByIdAsync(id))!;

        Assert.True(afterUpdate.UpdatedAtUtc > afterInsert.UpdatedAtUtc);
    }

    // --- 仕様書§3.3: 2フェーズ状態遷移 ----------------------------------------------------

    [Fact]
    public async Task 状態遷移_Planned_Executing_Completedと正しく記録更新される()
    {
        var record = CreateSampleRecord(state: OperationState.Planned);
        long id = await _repository.InsertAsync(record);
        Assert.Equal(OperationState.Planned, (await _repository.GetByIdAsync(id))!.State);

        await _repository.UpdateStateAsync(id, OperationState.Executing);
        Assert.Equal(OperationState.Executing, (await _repository.GetByIdAsync(id))!.State);

        await _repository.UpdateStateAsync(id, OperationState.Completed);
        var final = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, final!.State);
        Assert.Null(final.ErrorMessage);
    }

    [Fact]
    public async Task 状態遷移_Planned_Executing_Failedはエラーメッセージも記録される()
    {
        var record = CreateSampleRecord(state: OperationState.Planned);
        long id = await _repository.InsertAsync(record);

        await _repository.UpdateStateAsync(id, OperationState.Executing);
        await _repository.UpdateStateAsync(id, OperationState.Failed, "クラッシュによる中断（未完了）");

        var final = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, final!.State);
        Assert.Equal("クラッシュによる中断（未完了）", final.ErrorMessage);
    }

    [Fact]
    public async Task 状態遷移_Executing_Undoing_Undoneと正しく記録更新される()
    {
        var record = CreateSampleRecord(
            opType: OperationType.Move,
            state: OperationState.Completed,
            destinationPath: @"D:\organized\sample.pdf");
        long id = await _repository.InsertAsync(record);

        await _repository.UpdateStateAsync(id, OperationState.Undoing);
        Assert.Equal(OperationState.Undoing, (await _repository.GetByIdAsync(id))!.State);

        await _repository.UpdateStateAsync(id, OperationState.Undone);
        var final = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Undone, final!.State);
        Assert.Null(final.ErrorMessage);
    }

    [Fact]
    public async Task 状態遷移_Executing_Undoing_UndoFailedはエラーメッセージも記録される()
    {
        var record = CreateSampleRecord(state: OperationState.Completed);
        long id = await _repository.InsertAsync(record);

        await _repository.UpdateStateAsync(id, OperationState.Undoing);
        await _repository.UpdateStateAsync(id, OperationState.UndoFailed, "Undo処理中のクラッシュによる中断");

        var final = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.UndoFailed, final!.State);
        Assert.Equal("Undo処理中のクラッシュによる中断", final.ErrorMessage);
    }

    [Fact]
    public async Task 起動時リカバリ相当_Executing状態のレコードを走査して復旧できる()
    {
        // AI_IMPLEMENTATION_GUIDE.md §6 StartupRecoveryServiceが行う一連の流れを
        // Repository経由で再現し、GetRecordsByStateAsync + UpdateStateAsyncの組み合わせで
        // 破綻なく状態を復旧できることを確認する。
        var stale = CreateSampleRecord(opType: OperationType.Move, state: OperationState.Executing);
        long id = await _repository.InsertAsync(stale);

        var executingRecords = await _repository.GetRecordsByStateAsync(OperationState.Executing);
        Assert.Single(executingRecords);

        foreach (var r in executingRecords)
        {
            await _repository.UpdateStateAsync(r.Id, OperationState.Failed, "クラッシュによる中断（未完了）");
        }

        Assert.Empty(await _repository.GetRecordsByStateAsync(OperationState.Executing));
        var recovered = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, recovered!.State);
    }

    // --- SQLインジェクション対策 ------------------------------------------------------

    [Theory]
    [InlineData("'; DROP TABLE operation_history; --")]
    [InlineData("sample' OR '1'='1")]
    [InlineData("C:\\watch\\evil\"; DELETE FROM operation_history WHERE \"1\"=\"1")]
    public async Task パラメータ化クエリによりSQLインジェクション文字列が含まれても安全に保存復元できる(string maliciousText)
    {
        var record = CreateSampleRecord(sourcePath: maliciousText);
        record.LightweightHash = maliciousText;

        long id = await _repository.InsertAsync(record);
        var fetched = await _repository.GetByIdAsync(id);

        // 1. 悪意ある文字列がそのままリテラルとして保存・復元される（SQLとして解釈されていない）。
        Assert.NotNull(fetched);
        Assert.Equal(maliciousText, fetched!.SourcePath);
        Assert.Equal(maliciousText, fetched.LightweightHash);

        // 2. テーブル自体が破壊されていない（DROP TABLE等が実行されていない）。
        var recent = await _repository.GetRecentAsync(10);
        Assert.Contains(recent, r => r.Id == id);
    }

    [Fact]
    public async Task パラメータ化クエリによりOperationIdへのSQLインジェクション文字列でも検索できる()
    {
        const string maliciousOperationId = "op'; DROP TABLE operation_history; --";
        var record = CreateSampleRecord(operationId: maliciousOperationId);
        await _repository.InsertAsync(record);

        var fetched = await _repository.GetByOperationIdAsync(maliciousOperationId);

        Assert.NotNull(fetched);
        Assert.Equal(maliciousOperationId, fetched!.OperationId);
    }
}
