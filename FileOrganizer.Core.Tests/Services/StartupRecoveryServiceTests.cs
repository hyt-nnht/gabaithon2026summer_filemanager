using FileOrganizer.Core.Database;
using FileOrganizer.Core.Services;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Services;

/// <summary>
/// <see cref="IFileSystem"/>のフェイク実装。<c>File.Exists</c>相当の結果を実I/Oなしで
/// あらかじめ指定した集合に基づいて返す（存在するとみなすパスの集合を渡すのみ）。
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly HashSet<string> _existingPaths;

    public FakeFileSystem(params string[] existingPaths)
    {
        _existingPaths = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
    }

    public bool FileExists(string path) => _existingPaths.Contains(path);
}

/// <summary>
/// 仕様書§7.2-2「耐障害性」（移動中やDB書き込み中にプロセスが強制終了しても、次回起動時に
/// ファイル消失や不整合履歴が発生せず復旧できること）の受け入れ基準を検証する。
/// 対象: <see cref="StartupRecoveryService"/>（AI_IMPLEMENTATION_GUIDE.md §6準拠実装）。
/// 実際のIHistoryRepository（1-3で実装した<see cref="SqliteHistoryRepository"/>）に対して、
/// ファイル実在確認のみ<see cref="FakeFileSystem"/>でモック化し、Executing/Undoingで中断した
/// レコードが仕様どおりの状態へ復旧されることを確認する。
/// </summary>
public class StartupRecoveryServiceTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "StartupRecoveryService", Guid.NewGuid().ToString("N"));
    private readonly IHistoryRepository _repository;

    private const string SourcePath = @"C:\watch\sample.pdf";
    private const string DestinationPath = @"D:\organized\sample.pdf";

    public StartupRecoveryServiceTests()
    {
        Directory.CreateDirectory(_workDir);
        string connectionString = DatabaseInitializer.BuildConnectionString(Path.Combine(_workDir, "history.db"));
        new DatabaseInitializer(connectionString).InitializeAsync().GetAwaiter().GetResult();
        _repository = new SqliteHistoryRepository(connectionString);
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

    private async Task<long> SeedRecordAsync(
        OperationType opType,
        OperationState state,
        string sourcePath = SourcePath,
        string? destinationPath = DestinationPath)
    {
        var record = new HistoryRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OpType = opType,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            FileSizeBytes = 100,
            FileLastModifiedUtc = DateTime.UtcNow,
            LightweightHash = "HASH",
            State = state,
        };
        return await _repository.InsertAsync(record);
    }

    // --- Executing: Move / Rename -------------------------------------------------------

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Executing_MoveRenameで移動先のみ存在する場合はCompletedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Executing);
        var fileSystem = new FakeFileSystem(DestinationPath); // 移動先のみ存在、元パスは存在しない

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, record!.State);
        Assert.Null(record.ErrorMessage);
    }

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Executing_MoveRenameでどちらも存在しない場合はFailedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Executing);
        var fileSystem = new FakeFileSystem(); // どちらも存在しない

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, record!.State);
        Assert.Equal("クラッシュによる中断（未完了）", record.ErrorMessage);
    }

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Executing_MoveRenameで元パスのみ存在する場合はFailedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Executing);
        var fileSystem = new FakeFileSystem(SourcePath); // 未着手のまま中断（元パスのみ存在）

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, record!.State);
    }

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Executing_MoveRenameで両方存在する場合は不整合としてFailedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Executing);
        var fileSystem = new FakeFileSystem(SourcePath, DestinationPath); // 両方存在（コピー相当の不整合）

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, record!.State);
    }

    // --- Executing: Copy ------------------------------------------------------------------

    [Fact]
    public async Task Executing_Copyでコピー先が存在する場合はCompletedになる()
    {
        long id = await SeedRecordAsync(OperationType.Copy, OperationState.Executing);
        var fileSystem = new FakeFileSystem(SourcePath, DestinationPath); // Copyは元も残るのが正常

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, record!.State);
    }

    [Fact]
    public async Task Executing_Copyでコピー先が存在しない場合はFailedになる()
    {
        long id = await SeedRecordAsync(OperationType.Copy, OperationState.Executing);
        var fileSystem = new FakeFileSystem(SourcePath); // コピー先未生成のまま中断

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, record!.State);
        Assert.Equal("クラッシュによる中断（コピー未完了）", record.ErrorMessage);
    }

    // --- Executing: Recycle -----------------------------------------------------------------

    [Fact]
    public async Task Executing_Recycleで元ファイルが存在しない場合はCompletedになる()
    {
        long id = await SeedRecordAsync(OperationType.Recycle, OperationState.Executing, destinationPath: null);
        var fileSystem = new FakeFileSystem(); // ゴミ箱送り済み（元パスに存在しない）

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, record!.State);
    }

    [Fact]
    public async Task Executing_Recycleで元ファイルが存在する場合はFailedになる()
    {
        long id = await SeedRecordAsync(OperationType.Recycle, OperationState.Executing, destinationPath: null);
        var fileSystem = new FakeFileSystem(SourcePath); // ゴミ箱移動未完了

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Failed, record!.State);
        Assert.Equal("クラッシュによる中断（ゴミ箱移動未完了）", record.ErrorMessage);
    }

    // --- Undoing: Move / Rename -------------------------------------------------------------

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Undoing_MoveRenameで元パスのみ存在する場合はUndoneになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Undoing);
        var fileSystem = new FakeFileSystem(SourcePath); // Undo完了（元に戻り、移動先は残っていない）

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Undone, record!.State);
        Assert.Null(record.ErrorMessage);
    }

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Undoing_MoveRenameでどちらも存在しない場合はUndoFailedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Undoing);
        var fileSystem = new FakeFileSystem(); // ファイル自体が消失

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.UndoFailed, record!.State);
        Assert.Equal("Undo処理中のクラッシュによる中断", record.ErrorMessage);
    }

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Undoing_MoveRenameで両方存在する場合はUndoFailedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Undoing);
        var fileSystem = new FakeFileSystem(SourcePath, DestinationPath);

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.UndoFailed, record!.State);
    }

    [Theory]
    [InlineData(OperationType.Move)]
    [InlineData(OperationType.Rename)]
    public async Task Undoing_MoveRenameで移動先のみ存在する場合はUndoFailedになる(OperationType opType)
    {
        long id = await SeedRecordAsync(opType, OperationState.Undoing);
        var fileSystem = new FakeFileSystem(DestinationPath); // Undo未着手のまま中断

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.UndoFailed, record!.State);
    }

    // --- Undoing: Copy --------------------------------------------------------------------

    [Fact]
    public async Task Undoing_Copyでコピー先が削除済みの場合はUndoneになる()
    {
        long id = await SeedRecordAsync(OperationType.Copy, OperationState.Undoing);
        var fileSystem = new FakeFileSystem(SourcePath); // コピー先削除済み（Undo完了）

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Undone, record!.State);
    }

    [Fact]
    public async Task Undoing_Copyでコピー先が残存している場合はUndoFailedになる()
    {
        long id = await SeedRecordAsync(OperationType.Copy, OperationState.Undoing);
        var fileSystem = new FakeFileSystem(SourcePath, DestinationPath); // コピー先削除未完了

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.UndoFailed, record!.State);
        Assert.Equal("Undo処理中のクラッシュによるコピー先残存", record.ErrorMessage);
    }

    // --- Undoing: Recycle（未対応のUndo状態＝default分岐） --------------------------------

    [Fact]
    public async Task Undoing_Recycleは未対応のUndo状態としてUndoFailedになる()
    {
        long id = await SeedRecordAsync(OperationType.Recycle, OperationState.Undoing, destinationPath: null);
        var fileSystem = new FakeFileSystem(SourcePath); // ファイル状態に関わらずdefault分岐

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.UndoFailed, record!.State);
        Assert.Equal("未対応のUndo状態", record.ErrorMessage);
    }

    // --- 対象外の状態は変更されない ----------------------------------------------------------

    [Theory]
    [InlineData(OperationState.Planned)]
    [InlineData(OperationState.Completed)]
    [InlineData(OperationState.Failed)]
    [InlineData(OperationState.Undone)]
    [InlineData(OperationState.UndoFailed)]
    public async Task Executing_Undoing以外の状態のレコードは変更されない(OperationState state)
    {
        long id = await SeedRecordAsync(OperationType.Move, state);
        var fileSystem = new FakeFileSystem(); // どのファイルも存在しない状態でも対象外なら無関係

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(state, record!.State);
    }

    // --- 複数レコードの一括復旧・耐障害性の総合確認 -----------------------------------------

    [Fact]
    public async Task PerformStartupRecoveryAsync_複数のExecuting_Undoingレコードを一括で不整合なく復旧する()
    {
        long completedMoveId = await SeedRecordAsync(OperationType.Move, OperationState.Executing,
            sourcePath: @"C:\watch\a.pdf", destinationPath: @"D:\organized\a.pdf");
        long failedCopyId = await SeedRecordAsync(OperationType.Copy, OperationState.Executing,
            sourcePath: @"C:\watch\b.pdf", destinationPath: @"D:\organized\b.pdf");
        long undoneRenameId = await SeedRecordAsync(OperationType.Rename, OperationState.Undoing,
            sourcePath: @"C:\watch\c_old.pdf", destinationPath: @"C:\watch\c_new.pdf");
        long untouchedPlannedId = await SeedRecordAsync(OperationType.Move, OperationState.Planned,
            sourcePath: @"C:\watch\d.pdf", destinationPath: @"D:\organized\d.pdf");

        // a: 移動完了（移動先のみ存在） / b: コピー未完了（コピー先が存在しない）
        // c: Undo完了（元パスのみ存在） / d: 対象外（Plannedのまま）
        var fileSystem = new FakeFileSystem(@"D:\organized\a.pdf", @"C:\watch\c_old.pdf");

        await new StartupRecoveryService(_repository, fileSystem).PerformStartupRecoveryAsync();

        Assert.Equal(OperationState.Completed, (await _repository.GetByIdAsync(completedMoveId))!.State);
        Assert.Equal(OperationState.Failed, (await _repository.GetByIdAsync(failedCopyId))!.State);
        Assert.Equal(OperationState.Undone, (await _repository.GetByIdAsync(undoneRenameId))!.State);
        Assert.Equal(OperationState.Planned, (await _repository.GetByIdAsync(untouchedPlannedId))!.State);

        // 不整合な履歴（Executing/Undoingのまま残るレコード）が無いことを確認。
        Assert.Empty(await _repository.GetRecordsByStateAsync(OperationState.Executing));
        Assert.Empty(await _repository.GetRecordsByStateAsync(OperationState.Undoing));
    }

    // --- 実ファイル（一時フォルダ）によるE2E的な検証 ---------------------------------------

    [Fact]
    public async Task 実ファイルを使った場合でも_PhysicalFileSystem既定実装でMove完了を正しく判定する()
    {
        string sourcePath = Path.Combine(_workDir, "real-source.txt");
        string destinationPath = Path.Combine(_workDir, "real-dest.txt");
        File.WriteAllText(destinationPath, "moved"); // 移動先のみ実在させる（移動完了を模擬）

        long id = await SeedRecordAsync(OperationType.Move, OperationState.Executing, sourcePath, destinationPath);

        // fileSystem省略 → 既定のPhysicalFileSystem（実際のFile.Exists）が使われることの確認。
        await new StartupRecoveryService(_repository).PerformStartupRecoveryAsync();

        var record = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, record!.State);
    }
}
