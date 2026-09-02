using FileOrganizer.Core.Services;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Services;

/// <summary>
/// <see cref="IWatchSuppressor"/>のフェイク実装。<see cref="FileOperationService"/>が
/// どのパスにどんな冪等性トークンで抑止要求を出したかを記録する（実Watcherは起動しない）。
/// </summary>
internal sealed class FakeWatchSuppressor : IWatchSuppressor
{
    public List<(string Path, TimeSpan Duration, string Token)> Calls { get; } = new();

    public void SuppressPath(string path, TimeSpan duration, string idempotencyToken)
        => Calls.Add((path, duration, idempotencyToken));
}

/// <summary>
/// 仕様書§6「同名衝突の防止」「監視ループ防止」の受け入れ基準を検証する。
/// 対象: <see cref="FileOperationService"/>（内部で<c>SafeFileOperations</c>＝
/// AI_IMPLEMENTATION_GUIDE.md §4.1のCOM実装、または利用不可環境ではVB.FileIOフォールバックを利用）。
/// 実際のファイルI/Oを一時フォルダで行う（<see cref="Win32.SafeFileOperationsTests"/>と同様の方針）。
/// </summary>
public class FileOperationServiceTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "FileOperationService", Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _destDir;

    public FileOperationServiceTests()
    {
        _sourceDir = Path.Combine(_workDir, "source");
        _destDir = Path.Combine(_workDir, "dest");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);
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

    private string CreateSourceFile(string fileName, string content = "content")
    {
        string path = Path.Combine(_sourceDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateDestFile(string fileName, string content = "existing")
    {
        string path = Path.Combine(_destDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // --- MoveAsync: 基本動作 ------------------------------------------------------------

    [Fact]
    public async Task MoveAsync_衝突がなければ移動して成功する()
    {
        string sourcePath = CreateSourceFile("a.txt", "hello");
        var service = new FileOperationService();

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        Assert.True(result.Success);
        Assert.False(result.WasSkippedDueToConflict);
        string expectedPath = Path.Combine(_destDir, "a.txt");
        Assert.Equal(expectedPath, result.FinalPath);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(expectedPath));
        Assert.Equal("hello", File.ReadAllText(expectedPath));
    }

    [Fact]
    public async Task MoveAsync_移動元が存在しない場合は失敗する()
    {
        var service = new FileOperationService();
        string missingPath = Path.Combine(_sourceDir, "does-not-exist.txt");

        var result = await service.MoveAsync(missingPath, _destDir, ConflictPolicy.AutoRename);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // --- MoveAsync: 同名衝突の防止（仕様書§6） --------------------------------------------

    [Fact]
    public async Task MoveAsync_同名衝突時_AutoRenameなら連番付与され上書きされない()
    {
        string existingPath = CreateDestFile("a.txt", "original-in-dest");
        string sourcePath = CreateSourceFile("a.txt", "new-content");
        var service = new FileOperationService();

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        Assert.True(result.Success);
        Assert.False(result.WasSkippedDueToConflict);
        string expectedPath = Path.Combine(_destDir, "a_1.txt");
        Assert.Equal(expectedPath, result.FinalPath);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal("new-content", File.ReadAllText(expectedPath));
        // 既存ファイルは上書きされていない。
        Assert.Equal("original-in-dest", File.ReadAllText(existingPath));
    }

    [Fact]
    public async Task MoveAsync_同名衝突時_AutoRenameは既に連番が使われていれば次の番号にする()
    {
        CreateDestFile("a.txt");
        CreateDestFile("a_1.txt");
        string sourcePath = CreateSourceFile("a.txt", "new-content");
        var service = new FileOperationService();

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        Assert.Equal(Path.Combine(_destDir, "a_2.txt"), result.FinalPath);
    }

    [Fact]
    public async Task MoveAsync_同名衝突時_Skipなら何もせず成功扱いになる()
    {
        string existingPath = CreateDestFile("a.txt", "original-in-dest");
        string sourcePath = CreateSourceFile("a.txt", "new-content");
        var service = new FileOperationService();

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.Skip);

        Assert.True(result.Success);
        Assert.True(result.WasSkippedDueToConflict);
        Assert.Null(result.FinalPath);
        // 何も変化していない。
        Assert.True(File.Exists(sourcePath));
        Assert.Equal("original-in-dest", File.ReadAllText(existingPath));
    }

    [Fact]
    public async Task MoveAsync_同名衝突時_PromptUserなら操作を実行せず要確認を返す()
    {
        string existingPath = CreateDestFile("a.txt", "original-in-dest");
        string sourcePath = CreateSourceFile("a.txt", "new-content");
        var service = new FileOperationService();

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.PromptUser);

        Assert.False(result.Success);
        Assert.True(result.WasSkippedDueToConflict);
        Assert.NotNull(result.ErrorMessage);
        // 何も変化していない（ファイル操作は実行されていない）。
        Assert.True(File.Exists(sourcePath));
        Assert.Equal("original-in-dest", File.ReadAllText(existingPath));
    }

    // --- CopyAsync -----------------------------------------------------------------------

    [Fact]
    public async Task CopyAsync_衝突がなければコピーして成功し元ファイルも残る()
    {
        string sourcePath = CreateSourceFile("a.txt", "hello");
        var service = new FileOperationService();

        var result = await service.CopyAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        Assert.True(result.Success);
        string expectedPath = Path.Combine(_destDir, "a.txt");
        Assert.Equal(expectedPath, result.FinalPath);
        Assert.True(File.Exists(sourcePath)); // コピーなので元ファイルも残る
        Assert.True(File.Exists(expectedPath));
        Assert.Equal("hello", File.ReadAllText(expectedPath));
    }

    [Fact]
    public async Task CopyAsync_同名衝突時_AutoRenameなら連番付与される()
    {
        CreateDestFile("a.txt", "original-in-dest");
        string sourcePath = CreateSourceFile("a.txt", "new-content");
        var service = new FileOperationService();

        var result = await service.CopyAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        Assert.Equal(Path.Combine(_destDir, "a_1.txt"), result.FinalPath);
        Assert.True(File.Exists(sourcePath));
    }

    // --- RenameAsync: PathSanitizer適用（1-1連携） ------------------------------------------

    [Fact]
    public async Task RenameAsync_禁止文字を含む新名称はサニタイズされてからリネームされる()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var service = new FileOperationService();

        var result = await service.RenameAsync(sourcePath, "b?c*.txt");

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_sourceDir, "b_c_.txt"), result.FinalPath);
        Assert.True(File.Exists(result.FinalPath));
    }

    [Fact]
    public async Task RenameAsync_予約デバイス名はサニタイズされてからリネームされる()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var service = new FileOperationService();

        var result = await service.RenameAsync(sourcePath, "CON.txt");

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_sourceDir, "CON_file.txt"), result.FinalPath);
    }

    [Fact]
    public async Task RenameAsync_末尾のドットと空白はサニタイズされてからリネームされる()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var service = new FileOperationService();

        var result = await service.RenameAsync(sourcePath, "report .");

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_sourceDir, "report"), result.FinalPath);
    }

    [Fact]
    public async Task RenameAsync_サニタイズ後に現在名と一致する場合は無変更で成功する()
    {
        string sourcePath = CreateSourceFile("report.txt");
        var service = new FileOperationService();

        // 末尾スペースはサニタイズで除去され、結果的に現在名と同じになる。
        var result = await service.RenameAsync(sourcePath, "report.txt ");

        Assert.True(result.Success);
        Assert.Equal(sourcePath, result.FinalPath);
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task RenameAsync_大文字小文字のみの変更は衝突と誤検知されずリネームされる()
    {
        string sourcePath = CreateSourceFile("report.txt");
        var service = new FileOperationService();

        var result = await service.RenameAsync(sourcePath, "REPORT.txt");

        Assert.True(result.Success);
        Assert.False(result.WasSkippedDueToConflict);
        string actualFileName = Path.GetFileName(Directory.EnumerateFiles(_sourceDir).Single());
        Assert.Equal("REPORT.txt", actualFileName);
    }

    [Fact]
    public async Task RenameAsync_存在しないファイルは失敗する()
    {
        var service = new FileOperationService();
        var result = await service.RenameAsync(Path.Combine(_sourceDir, "missing.txt"), "new.txt");

        Assert.False(result.Success);
    }

    // --- RenameAsync: 同名衝突の防止（コンストラクタのrenameConflictPolicyで制御） -----------

    [Fact]
    public async Task RenameAsync_同名衝突時_既定のAutoRenameで連番付与される()
    {
        CreateSourceFile("b.txt", "existing-b");
        string sourcePath = CreateSourceFile("a.txt", "content-a");
        var service = new FileOperationService(); // 既定 renameConflictPolicy = AutoRename

        var result = await service.RenameAsync(sourcePath, "b.txt");

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_sourceDir, "b_1.txt"), result.FinalPath);
        Assert.Equal("existing-b", File.ReadAllText(Path.Combine(_sourceDir, "b.txt")));
    }

    [Fact]
    public async Task RenameAsync_同名衝突時_Skipポリシー指定なら何もせず成功扱いになる()
    {
        CreateSourceFile("b.txt", "existing-b");
        string sourcePath = CreateSourceFile("a.txt", "content-a");
        var service = new FileOperationService(renameConflictPolicy: ConflictPolicy.Skip);

        var result = await service.RenameAsync(sourcePath, "b.txt");

        Assert.True(result.Success);
        Assert.True(result.WasSkippedDueToConflict);
        Assert.True(File.Exists(sourcePath)); // リネームされていない
    }

    [Fact]
    public async Task RenameAsync_同名衝突時_PromptUserポリシー指定なら操作を実行せず要確認を返す()
    {
        CreateSourceFile("b.txt", "existing-b");
        string sourcePath = CreateSourceFile("a.txt", "content-a");
        var service = new FileOperationService(renameConflictPolicy: ConflictPolicy.PromptUser);

        var result = await service.RenameAsync(sourcePath, "b.txt");

        Assert.False(result.Success);
        Assert.True(result.WasSkippedDueToConflict);
        Assert.True(File.Exists(sourcePath)); // リネームされていない
    }

    // --- RecycleAsync ----------------------------------------------------------------------

    [Fact]
    public async Task RecycleAsync_成功するとファイルが元の場所からなくなる()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var service = new FileOperationService();

        var result = await service.RecycleAsync(sourcePath);

        Assert.True(result.Success);
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task RecycleAsync_存在しない対象は失敗する()
    {
        var service = new FileOperationService();
        var result = await service.RecycleAsync(Path.Combine(_sourceDir, "missing.txt"));

        Assert.False(result.Success);
    }

    // --- 監視ループ防止（IWatchSuppressorへの冪等性トークン通知、仕様書§6） -------------------

    [Fact]
    public async Task MoveAsync_成功時にWatcherへ移動先パスと冪等性トークンを通知する()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor, TimeSpan.FromSeconds(5));

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        var call = Assert.Single(suppressor.Calls);
        Assert.Equal(result.FinalPath, call.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Duration);
        Assert.False(string.IsNullOrWhiteSpace(call.Token));
    }

    [Fact]
    public async Task CopyAsync_成功時にWatcherへコピー先パスと冪等性トークンを通知する()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor);

        var result = await service.CopyAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        var call = Assert.Single(suppressor.Calls);
        Assert.Equal(result.FinalPath, call.Path);
    }

    [Fact]
    public async Task RenameAsync_成功時にWatcherへリネーム後パスと冪等性トークンを通知する()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor);

        var result = await service.RenameAsync(sourcePath, "b.txt");

        var call = Assert.Single(suppressor.Calls);
        Assert.Equal(result.FinalPath, call.Path);
    }

    [Fact]
    public async Task 操作ごとに異なる冪等性トークンが発行される()
    {
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor);

        string source1 = CreateSourceFile("a.txt");
        string source2 = CreateSourceFile("b.txt");
        await service.MoveAsync(source1, _destDir, ConflictPolicy.AutoRename);
        await service.MoveAsync(source2, _destDir, ConflictPolicy.AutoRename);

        Assert.Equal(2, suppressor.Calls.Count);
        Assert.NotEqual(suppressor.Calls[0].Token, suppressor.Calls[1].Token);
    }

    [Fact]
    public async Task MoveAsync_Skipで何もしない場合はWatcherへ通知しない()
    {
        CreateDestFile("a.txt");
        string sourcePath = CreateSourceFile("a.txt");
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor);

        await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.Skip);

        Assert.Empty(suppressor.Calls);
    }

    [Fact]
    public async Task MoveAsync_PromptUserで操作しない場合はWatcherへ通知しない()
    {
        CreateDestFile("a.txt");
        string sourcePath = CreateSourceFile("a.txt");
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor);

        await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.PromptUser);

        Assert.Empty(suppressor.Calls);
    }

    [Fact]
    public async Task RecycleAsync_はWatcherへ通知しない()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var suppressor = new FakeWatchSuppressor();
        var service = new FileOperationService(suppressor);

        await service.RecycleAsync(sourcePath);

        Assert.Empty(suppressor.Calls);
    }

    [Fact]
    public async Task WatchSuppressorを渡さなくても例外を投げず動作する()
    {
        string sourcePath = CreateSourceFile("a.txt");
        var service = new FileOperationService(watchSuppressor: null);

        var result = await service.MoveAsync(sourcePath, _destDir, ConflictPolicy.AutoRename);

        Assert.True(result.Success);
    }
}
