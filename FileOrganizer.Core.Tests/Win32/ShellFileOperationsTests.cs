using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FileOrganizer.Core.Win32;

namespace FileOrganizer.Core.Tests.Win32;

/// <summary>
/// 仕様書§3.2「不可逆な物理削除の禁止」「Cross-Volume非同期移動」の検証。
/// AI_IMPLEMENTATION_GUIDE.md §4.1 のSTAワーカープール内包型 <see cref="ShellFileOperations"/> を検証する。
/// 同一ボリューム内での自動テストに加え、実ドライブをまたぐCross-Volume移動・非ブロッキング性の
/// 手動確認は <c>FileOrganizer.Core.SmokeTest</c>（<c>--shell-ops</c>モード）を参照。
/// </summary>
/// <remarks>
/// 【既知の環境依存事項】<c>IFileOperation</c> はOSレベルのCOMアクティブ化に依存する。
/// 一部のPC（セキュリティソフト/グループポリシー/OSビルド起因の可能性がある）では
/// <c>(IFileOperation)new FileOperation()</c> のQueryInterfaceが <c>E_NOINTERFACE (0x80004002)</c>
/// で失敗することを確認済み（.NET Core / .NET Framework 双方、バックグラウンドSTAスレッド /
/// プロセスのメインSTAスレッド双方で再現・切り分け済み。コードの実装起因ではない）。
/// この場合 <see cref="ShellFileOperations"/> 側の<c>catch { return false; }</c>により
/// 本クラスの各メソッドは例外を投げず静かに<c>false</c>を返すため、下記テストは
/// <see cref="Prerequisite_IFileOperationComActivation_IsAvailable"/> の失敗メッセージで
/// 環境要因である旨を確認したうえで読むこと。
/// </remarks>
public class ShellFileOperationsTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", Guid.NewGuid().ToString("N"));

    public ShellFileOperationsTests()
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
            // テスト後片付けの失敗は握りつぶす（一時フォルダなので実害なし）。
        }
    }

    private string CreateTestFile(string? fileName = null, string content = "shell-file-operations-test")
    {
        fileName ??= $"test-{Guid.NewGuid():N}.txt";
        string path = Path.Combine(_workDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// 前提条件チェック: このマシンでCOM経由の<c>IFileOperation</c>アクティブ化自体が可能かを確認する。
    /// これが失敗する場合、他のテストの失敗（<c>SendToRecycleBinAsync</c>/<c>MoveFileSafelyAsync</c>が
    /// 例外を投げず<c>false</c>を返す）は<see cref="ShellFileOperations"/>のバグではなく、
    /// OS/セキュリティソフト等の環境要因によりQueryInterfaceが<c>E_NOINTERFACE</c>で
    /// 失敗していることが原因である可能性が高い（.NET Core・.NET Framework双方、
    /// バックグラウンドSTAスレッド・プロセスのメインSTAスレッド双方で同一の失敗を確認済み）。
    /// </summary>
    [Fact]
    public void Prerequisite_IFileOperationComActivation_IsAvailable()
    {
        Exception? activationError = RunOnSta<Exception?>(() =>
        {
            try
            {
                object fileOperation = ActivateFileOperationCoClass();
                Marshal.ReleaseComObject(fileOperation);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.True(
            activationError is null,
            "このマシンでは IFileOperation の COMアクティブ化（QueryInterface）が失敗しました。" +
            "ShellFileOperations.cs（AI_IMPLEMENTATION_GUIDE.md §4.1をそのまま転記したコード）の" +
            "バグではなく、OS/セキュリティソフト/グループポリシー等の環境要因の可能性が高い" +
            "（.NET Framework側でも同一の失敗を別途確認済み）。詳細: " + activationError);
    }

    [Fact]
    public async Task MoveFileSafelyAsync_CreatesDestinationDirectory_AndMovesFile()
    {
        string sourcePath = CreateTestFile();
        string fileName = Path.GetFileName(sourcePath);
        // ネストした未作成ディレクトリを移動先に指定し、自動作成を検証する。
        string destinationDir = Path.Combine(_workDir, "dest", "nested", "subdir");

        Assert.False(Directory.Exists(destinationDir));

        bool result = await ShellFileOperations.MoveFileSafelyAsync(sourcePath, destinationDir);

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

        bool result = await ShellFileOperations.MoveFileSafelyAsync(sourcePath, destinationDir);

        Assert.False(result);
    }

    [Fact]
    public async Task MoveFileSafelyAsync_PreCanceledToken_PropagatesOperationCanceledException()
    {
        string sourcePath = CreateTestFile();
        string destinationDir = Path.Combine(_workDir, "dest");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellFileOperations.MoveFileSafelyAsync(sourcePath, destinationDir, cancellationToken: cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
        // キャンセルされたのでファイルは動いていないはず。
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task SendToRecycleBinAsync_MovesFileToRecycleBin_AndSourceNoLongerExists()
    {
        // ごみ箱内で一意に識別できるよう、ファイル名にGUIDを含める。
        string fileName = $"shelltest-{Guid.NewGuid():N}.txt";
        string path = CreateTestFile(fileName);

        bool result = await ShellFileOperations.SendToRecycleBinAsync(path);

        Assert.True(result);
        Assert.False(File.Exists(path), "「不可逆な物理削除の禁止」: 移動元にファイルが残っています（=正しく退避されていない）。");

        // File.Delete等の不可逆削除ではなく、実際にごみ箱へ退避されたことをShell.Application COM経由で確認する。
        bool foundInRecycleBin = TryFindAndPurgeFromRecycleBin(fileName);
        Assert.True(foundInRecycleBin, "ごみ箱内にファイルが見つかりませんでした（物理削除された可能性があります）。");
    }

    [Fact]
    public async Task SendToRecycleBinAsync_NonExistentFile_ReturnsFalseWithoutThrowing()
    {
        string path = Path.Combine(_workDir, "does-not-exist.txt");

        bool result = await ShellFileOperations.SendToRecycleBinAsync(path);

        Assert.False(result);
    }

    [Fact]
    public async Task SendToRecycleBinAsync_PreCanceledToken_PropagatesOperationCanceledException_AndDoesNotDeleteFile()
    {
        string path = CreateTestFile();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellFileOperations.SendToRecycleBinAsync(path, cancellationToken: cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// Shell.Application COM自動化（<c>Namespace(10)</c> = ごみ箱）で指定ファイル名のアイテムを探す。
    /// 見つかった場合はテスト環境を汚さないよう、その場で完全に削除（後始末）する。
    /// COM呼び出しはSTAスレッド上で行う必要があるため専用スレッドで実行する。
    /// </summary>
    private static bool TryFindAndPurgeFromRecycleBin(string fileName)
    {
        return RunOnSta(() =>
        {
            Type? shellAppType = Type.GetTypeFromProgID("Shell.Application")
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
                            item.InvokeVerb("delete"); // ごみ箱内アイテムに対するdelete = 完全削除（後始末）
                        }
                        catch
                        {
                            // 後始末に失敗してもテスト結果には影響しない。
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
        });
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        ExceptionDispatchInfo? capturedException = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        capturedException?.Throw();
        return result;
    }

    /// <summary>
    /// <see cref="ShellFileOperations"/>内部の <c>(IFileOperation)new FileOperation()</c> と等価な
    /// COMアクティブ化＋QueryInterfaceのみを行う（<see cref="Prerequisite_IFileOperationComActivation_IsAvailable"/>専用）。
    /// 型はASTAワーカースレッド上でのみ呼び出すこと。
    /// </summary>
    private static object ActivateFileOperationCoClass() => (IFileOperationProbe)new FileOperationCoClassProbe();

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")] // CLSID_FileOperation
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOperationCoClassProbe
    {
    }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf50368389b")] // IID_IFileOperation
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProbe
    {
        uint Advise(IntPtr pfops);
    }
}
