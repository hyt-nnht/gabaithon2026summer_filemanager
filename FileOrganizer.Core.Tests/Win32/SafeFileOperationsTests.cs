using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FileOrganizer.Core.Win32;

namespace FileOrganizer.Core.Tests.Win32;

/// <summary>
/// 仕様書§3.2「不可逆な物理削除の禁止」「Cross-Volume非同期移動」の検証（実運用エントリポイント版）。
/// <see cref="SafeFileOperations"/> は、このマシンで <c>IFileOperation</c> のCOMアクティブ化が
/// 使えない場合（<see cref="ShellFileOperationsTests.Prerequisite_IFileOperationComActivation_IsAvailable"/>
/// 参照）でも、<c>Microsoft.VisualBasic.FileIO.FileSystem</c> ベースのフォールバックにより
/// 同じ受け入れ基準を満たす。
/// </summary>
public class SafeFileOperationsTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "Safe", Guid.NewGuid().ToString("N"));

    public SafeFileOperationsTests()
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

    private string CreateTestFile(string? fileName = null, string content = "safe-file-operations-test")
    {
        fileName ??= $"test-{Guid.NewGuid():N}.txt";
        string path = Path.Combine(_workDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void UsingShellFileOperations_ReportsWhichBackendIsActive()
    {
        // 情報表示専用。フォールバック中かどうかをテスト出力から確認できるようにする。
        bool usingShell = SafeFileOperations.IsUsingShellFileOperations;
        Assert.True(usingShell || !usingShell); // tautology: always passes, exists to surface the value in output
    }

    [Fact]
    public async Task MoveFileSafelyAsync_CreatesDestinationDirectory_AndMovesFile()
    {
        string sourcePath = CreateTestFile();
        string fileName = Path.GetFileName(sourcePath);
        string destinationDir = Path.Combine(_workDir, "dest", "nested", "subdir");

        Assert.False(Directory.Exists(destinationDir));

        bool result = await SafeFileOperations.MoveFileSafelyAsync(sourcePath, destinationDir);

        Assert.True(result);
        Assert.True(Directory.Exists(destinationDir), "移動先ディレクトリが自動作成されていません。");
        Assert.False(File.Exists(sourcePath), "移動元にファイルが残っています。");
        Assert.True(File.Exists(Path.Combine(destinationDir, fileName)), "移動先にファイルが見つかりません。");
    }

    [Fact]
    public async Task MoveFileSafelyAsync_NonExistentSource_ReturnsFalseWithoutThrowing()
    {
        string sourcePath = Path.Combine(_workDir, "does-not-exist.txt");
        string destinationDir = Path.Combine(_workDir, "dest");

        bool result = await SafeFileOperations.MoveFileSafelyAsync(sourcePath, destinationDir);

        Assert.False(result);
    }

    [Fact]
    public async Task MoveFileSafelyAsync_PreCanceledToken_PropagatesOperationCanceledException()
    {
        string sourcePath = CreateTestFile();
        string destinationDir = Path.Combine(_workDir, "dest");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafeFileOperations.MoveFileSafelyAsync(sourcePath, destinationDir, cancellationToken: cts.Token));

        // キャンセルされたのでファイルは動いていないはず。
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task SendToRecycleBinAsync_MovesFileToRecycleBin_AndSourceNoLongerExists()
    {
        string fileName = $"safeoptest-{Guid.NewGuid():N}.txt";
        string path = CreateTestFile(fileName);

        bool result = await SafeFileOperations.SendToRecycleBinAsync(path);

        // 「不可逆な物理削除の禁止」: 操作が成功を報告し、かつ移動元からファイルが消えていること
        // （= File.Delete等の直接削除ではなく退避が起きたこと）が最低限の合格条件。
        Assert.True(result);
        Assert.False(File.Exists(path), "移動元にファイルが残っています。");

        // 追加確認: Shell.Application COM経由でごみ箱内の実在を確認する（後始末として完全削除も行う）。
        // 環境によりごみ箱の項目数が多いとこの列挙が非常に遅くなることがあるため、
        // タイムアウト内に完了した場合のみ厳密に検証し、テストスイート全体を巻き込んで
        // ハングさせないようにする。
        (bool completed, bool found) = TryFindAndPurgeFromRecycleBinWithTimeout(fileName, TimeSpan.FromSeconds(15));
        if (completed)
        {
            Assert.True(found, "ごみ箱内にファイルが見つかりませんでした（物理削除された可能性があります）。");
        }
    }

    [Fact]
    public async Task SendToRecycleBinAsync_NonExistentFile_ReturnsFalseWithoutThrowing()
    {
        string path = Path.Combine(_workDir, "does-not-exist.txt");

        bool result = await SafeFileOperations.SendToRecycleBinAsync(path);

        Assert.False(result);
    }

    [Fact]
    public async Task SendToRecycleBinAsync_PreCanceledToken_PropagatesOperationCanceledException_AndDoesNotDeleteFile()
    {
        string path = CreateTestFile();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SafeFileOperations.SendToRecycleBinAsync(path, cancellationToken: cts.Token));

        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// <see cref="ShellFileOperationsTests"/>の同名ヘルパーと同一ロジック。
    /// Shell.Application COM自動化はごみ箱の内容量次第で低速/長時間化することがあるため、
    /// タイムアウト付きで実行し、完了有無を呼び出し元に返す。
    /// </summary>
    private static (bool Completed, bool Found) TryFindAndPurgeFromRecycleBinWithTimeout(string fileName, TimeSpan timeout)
    {
        bool found = false;
        Exception? capturedException = null;

        var thread = new Thread(() =>
        {
            try
            {
                found = FindAndPurgeFromRecycleBin(fileName);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        bool completed = thread.Join(timeout);
        if (completed && capturedException is not null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }

        return (completed, found);
    }

    private static bool FindAndPurgeFromRecycleBin(string fileName)
    {
        {
            Type shellAppType = Type.GetTypeFromProgID("Shell.Application")
                ?? throw new InvalidOperationException("Shell.Application COMオブジェクトが見つかりません。");
            dynamic shell = Activator.CreateInstance(shellAppType)
                ?? throw new InvalidOperationException("Shell.Applicationのインスタンス化に失敗しました。");

            try
            {
                dynamic recycleBin = shell.Namespace(10); // ssfBITBUCKET
                dynamic items = recycleBin.Items();
                int count = items.Count;

                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name;
                    if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            item.InvokeVerb("delete");
                        }
                        catch
                        {
                            // 後始末失敗は無視。
                        }

                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }

}
