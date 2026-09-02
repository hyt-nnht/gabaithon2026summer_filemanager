using FileOrganizer.Core.Database;
using FileOrganizer.Core.Engine;
using FileOrganizer.Core.Services;
using FileOrganizer.Core.Utils;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Engine;

/// <summary>
/// 仕様書§3.2「操作別Undo仕様と競合制御」の受け入れ基準を検証する。
/// 対象: <see cref="UndoManager"/>。1-3 <see cref="SqliteHistoryRepository"/>（一時DB）・
/// 1-8 <see cref="FileOperationService"/>はいずれも実実装を使用し、実際のファイルI/Oを一時フォルダで行う。
/// </summary>
public class UndoManagerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "UndoManager", Guid.NewGuid().ToString("N"));
    private readonly IHistoryRepository _repository;
    private readonly IFileOperationService _fileOperationService;

    public UndoManagerTests()
    {
        Directory.CreateDirectory(_workDir);
        string connectionString = DatabaseInitializer.BuildConnectionString(Path.Combine(_workDir, "history.db"));
        new DatabaseInitializer(connectionString).InitializeAsync().GetAwaiter().GetResult();
        _repository = new SqliteHistoryRepository(connectionString);
        _fileOperationService = new FileOperationService();
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

    private UndoManager CreateUndoManager(ConflictPolicy moveRestoreConflictPolicy = ConflictPolicy.PromptUser)
        => new(_repository, _fileOperationService, moveRestoreConflictPolicy);

    /// <summary>
    /// 「元操作の結果」を実ファイルとして用意し、それに対応するCompleted状態のHistoryRecordを
    /// 実DBへ挿入する（LightweightHashは実ファイルの内容から正しく計算する）。
    /// </summary>
    private async Task<long> SeedCompletedRecordAsync(
        OperationType opType,
        string sourcePath,
        string? destinationPath,
        string currentFileContentPath)
    {
        string hash = HashHelper.ComputeLightweightHash(currentFileContentPath);
        var record = new HistoryRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OpType = opType,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            FileSizeBytes = new FileInfo(currentFileContentPath).Length,
            FileLastModifiedUtc = File.GetLastWriteTimeUtc(currentFileContentPath),
            LightweightHash = hash,
            State = OperationState.Completed,
        };
        return await _repository.InsertAsync(record);
    }

    // --- 移動(Move)のUndo ------------------------------------------------------------------

    [Fact]
    public async Task UndoAsync_Moveは移動先から元パスへ実際にファイルを戻す()
    {
        // Moveはファイル名を変えないため、元パス・移動先パスは同じファイル名で
        // ディレクトリのみが異なる（AI_IMPLEMENTATION_GUIDE.md準拠のOperationType.Move意味論）。
        string srcDir = Path.Combine(_workDir, "src");
        string dstDir = Path.Combine(_workDir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        string originalPath = Path.Combine(srcDir, "report.txt");
        string movedPath = Path.Combine(dstDir, "report.txt");
        File.WriteAllText(movedPath, "content"); // 「移動済み」の状態を再現（originalは既に存在しない）

        long id = await SeedCompletedRecordAsync(OperationType.Move, originalPath, movedPath, movedPath);
        var undoManager = CreateUndoManager();

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Success, result.Outcome);
        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(movedPath));
        Assert.Equal("content", File.ReadAllText(originalPath));

        var persisted = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Undone, persisted!.State);
    }

    [Fact]
    public async Task UndoAsync_Moveでハッシュ不一致なら確認要求を返し状態を変更しない()
    {
        string srcDir = Path.Combine(_workDir, "src");
        string dstDir = Path.Combine(_workDir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        string originalPath = Path.Combine(srcDir, "report.txt");
        string movedPath = Path.Combine(dstDir, "report.txt");
        File.WriteAllText(movedPath, "content-at-record-time");

        long id = await SeedCompletedRecordAsync(OperationType.Move, originalPath, movedPath, movedPath);

        // 記録後にユーザーがファイルを編集した状況を再現。
        File.WriteAllText(movedPath, "content-changed-by-user");

        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.RequiresConfirmation, result.Outcome);
        Assert.NotNull(result.Message);
        Assert.True(File.Exists(movedPath)); // 何も動いていない
        Assert.False(File.Exists(originalPath));

        var persisted = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, persisted!.State); // 状態は変化しない
    }

    [Fact]
    public async Task UndoAsync_Moveで復元先に別ファイルが存在し既定ポリシーなら確認要求を返す()
    {
        string srcDir = Path.Combine(_workDir, "src");
        string dstDir = Path.Combine(_workDir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        string originalPath = Path.Combine(srcDir, "report.txt");
        string movedPath = Path.Combine(dstDir, "report.txt");
        File.WriteAllText(movedPath, "content");
        File.WriteAllText(originalPath, "someone else's file"); // 復元先に既に別ファイル

        long id = await SeedCompletedRecordAsync(OperationType.Move, originalPath, movedPath, movedPath);
        var undoManager = CreateUndoManager(); // 既定 = PromptUser

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.RequiresConfirmation, result.Outcome);
        Assert.Equal("someone else's file", File.ReadAllText(originalPath)); // 上書きされていない
        Assert.True(File.Exists(movedPath)); // 移動元も変化なし

        var persisted = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Completed, persisted!.State);
    }

    [Fact]
    public async Task UndoAsync_MoveでAutoRenameポリシー指定なら別名で復元される()
    {
        string srcDir = Path.Combine(_workDir, "src");
        string dstDir = Path.Combine(_workDir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        string originalPath = Path.Combine(srcDir, "report.txt");
        string movedPath = Path.Combine(dstDir, "report.txt");
        File.WriteAllText(movedPath, "content");
        File.WriteAllText(originalPath, "someone else's file");

        long id = await SeedCompletedRecordAsync(OperationType.Move, originalPath, movedPath, movedPath);
        var undoManager = CreateUndoManager(ConflictPolicy.AutoRename);

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Success, result.Outcome);
        string expectedAltPath = Path.Combine(srcDir, "report_1.txt");
        Assert.True(File.Exists(expectedAltPath));
        Assert.Equal("content", File.ReadAllText(expectedAltPath));
        Assert.Equal("someone else's file", File.ReadAllText(originalPath)); // 既存ファイルは維持
    }

    // --- リネーム(Rename)のUndo -------------------------------------------------------------

    [Fact]
    public async Task UndoAsync_Renameは現在の名称から元の名称へ実際に戻す()
    {
        string originalPath = Path.Combine(_workDir, "original.txt");
        string renamedPath = Path.Combine(_workDir, "renamed.txt");
        File.WriteAllText(renamedPath, "content");

        long id = await SeedCompletedRecordAsync(OperationType.Rename, originalPath, renamedPath, renamedPath);
        var undoManager = CreateUndoManager();

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Success, result.Outcome);
        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(renamedPath));

        var persisted = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Undone, persisted!.State);
    }

    [Fact]
    public async Task UndoAsync_Renameは元の名前が使用中なら_AutoRenameポリシー指定でも常に確認要求になる()
    {
        string originalPath = Path.Combine(_workDir, "original.txt");
        string renamedPath = Path.Combine(_workDir, "renamed.txt");
        File.WriteAllText(renamedPath, "content");
        File.WriteAllText(originalPath, "someone else's file"); // 元の名前が既に使用中

        long id = await SeedCompletedRecordAsync(OperationType.Rename, originalPath, renamedPath, renamedPath);
        // Moveなら別名復元されるはずのAutoRenameポリシーを渡しても、Renameには適用されないことを確認する。
        var undoManager = CreateUndoManager(ConflictPolicy.AutoRename);

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.RequiresConfirmation, result.Outcome);
        Assert.True(File.Exists(renamedPath)); // 何も動いていない
        Assert.Equal("someone else's file", File.ReadAllText(originalPath));
    }

    // --- コピー(Copy)のUndo -----------------------------------------------------------------

    [Fact]
    public async Task UndoAsync_Copyはコピー先をゴミ箱へ送り元ファイルは変更しない()
    {
        string originalPath = Path.Combine(_workDir, "original.txt");
        string copiedPath = Path.Combine(_workDir, "copied.txt");
        File.WriteAllText(originalPath, "content");
        File.WriteAllText(copiedPath, "content"); // コピー先（元と同内容）

        long id = await SeedCompletedRecordAsync(OperationType.Copy, originalPath, copiedPath, copiedPath);
        var undoManager = CreateUndoManager();

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Success, result.Outcome);
        Assert.False(File.Exists(copiedPath)); // ゴミ箱送りされた
        Assert.True(File.Exists(originalPath)); // 元ファイルは変更なし
        Assert.Equal("content", File.ReadAllText(originalPath));

        var persisted = await _repository.GetByIdAsync(id);
        Assert.Equal(OperationState.Undone, persisted!.State);
    }

    [Fact]
    public async Task UndoAsync_Copyでコピー先がユーザーにより更新されている場合は確認要求になる()
    {
        string originalPath = Path.Combine(_workDir, "original.txt");
        string copiedPath = Path.Combine(_workDir, "copied.txt");
        File.WriteAllText(originalPath, "content");
        File.WriteAllText(copiedPath, "content");

        long id = await SeedCompletedRecordAsync(OperationType.Copy, originalPath, copiedPath, copiedPath);

        File.WriteAllText(copiedPath, "user-edited-the-copy"); // コピー先をユーザーが更新

        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.RequiresConfirmation, result.Outcome);
        Assert.True(File.Exists(copiedPath)); // ゴミ箱送りされていない
    }

    // --- ゴミ箱送り(Recycle)は対象外 ---------------------------------------------------------

    [Fact]
    public async Task UndoAsync_Recycleはアプリ内Undo対象外としてFailedを返す()
    {
        string originalPath = Path.Combine(_workDir, "original.txt");
        // Recycle済みを模擬（ハッシュ計算用に一時ファイルを使うが、実際のUndo経路には使われない）。
        string tempForHash = Path.Combine(_workDir, "temp-for-hash.txt");
        File.WriteAllText(tempForHash, "content");

        long id = await SeedCompletedRecordAsync(OperationType.Recycle, originalPath, null, tempForHash);
        var undoManager = CreateUndoManager();

        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Failed, result.Outcome);
        Assert.Contains("ゴミ箱", result.Message);
    }

    // --- 状態・引数の検証 -------------------------------------------------------------------

    [Fact]
    public async Task UndoAsync_Completed以外の状態はFailedを返す()
    {
        string sourcePath = Path.Combine(_workDir, "a.txt");
        File.WriteAllText(sourcePath, "content");
        var record = new HistoryRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OpType = OperationType.Move,
            SourcePath = sourcePath,
            DestinationPath = Path.Combine(_workDir, "b.txt"),
            FileSizeBytes = 1,
            FileLastModifiedUtc = DateTime.UtcNow,
            LightweightHash = "x",
            State = OperationState.Planned,
        };
        long id = await _repository.InsertAsync(record);

        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task UndoAsync_存在しないIdはFailedを返す()
    {
        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync(999_999L);

        Assert.Equal(UndoOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task UndoAsync_存在しないOperationIdはFailedを返す()
    {
        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync("does-not-exist");

        Assert.Equal(UndoOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task UndoAsync_OperationId指定でも同様にUndoできる()
    {
        string srcDir = Path.Combine(_workDir, "src");
        string dstDir = Path.Combine(_workDir, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        string originalPath = Path.Combine(srcDir, "report.txt");
        string movedPath = Path.Combine(dstDir, "report.txt");
        File.WriteAllText(movedPath, "content");

        string hash = HashHelper.ComputeLightweightHash(movedPath);
        var record = new HistoryRecord
        {
            OperationId = "my-operation-id",
            OpType = OperationType.Move,
            SourcePath = originalPath,
            DestinationPath = movedPath,
            FileSizeBytes = new FileInfo(movedPath).Length,
            FileLastModifiedUtc = File.GetLastWriteTimeUtc(movedPath),
            LightweightHash = hash,
            State = OperationState.Completed,
        };
        await _repository.InsertAsync(record);

        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync("my-operation-id");

        Assert.Equal(UndoOutcome.Success, result.Outcome);
        Assert.True(File.Exists(originalPath));
    }

    [Fact]
    public async Task UndoAsync_対象ファイルが既に消失している場合はFailedを返す()
    {
        string originalPath = Path.Combine(_workDir, "original.txt");
        string movedPath = Path.Combine(_workDir, "moved.txt"); // 実際には作らない（消失を模擬）

        var record = new HistoryRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OpType = OperationType.Move,
            SourcePath = originalPath,
            DestinationPath = movedPath,
            FileSizeBytes = 1,
            FileLastModifiedUtc = DateTime.UtcNow,
            LightweightHash = "anyhash",
            State = OperationState.Completed,
        };
        long id = await _repository.InsertAsync(record);

        var undoManager = CreateUndoManager();
        var result = await undoManager.UndoAsync(id);

        Assert.Equal(UndoOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void Constructor_historyRepositoryがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() => new UndoManager(null!, _fileOperationService));
    }
}
