using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace FileOrganizer.Core.Win32;

/// <summary>
/// <see cref="ShellFileOperations"/>（AI_IMPLEMENTATION_GUIDE.md §4.1, COM <c>IFileOperation</c>実装）の
/// フォールバック層。仕様書§3.2「不可逆な物理削除の禁止」「Cross-Volume非同期移動」を満たすための
/// 実運用向けエントリポイント。<b>アプリ本体からは <see cref="ShellFileOperations"/> を直接呼ばず、
/// 常にこちらを使うこと。</b>
/// </summary>
/// <remarks>
/// 【背景】複数のWindows実機で、<c>IFileOperation</c>のCOMアクティブ化（QueryInterface）が
/// <c>E_NOINTERFACE (0x80004002)</c>で失敗する事象を確認した。.NET Core/.NET Framework双方、
/// バックグラウンドSTAスレッド/プロセスのメインSTAスレッド双方、
/// <c>new FileOperation()+cast</c>/<c>Type.GetTypeFromCLSID</c>のどちらの生成方法でも再現し、
/// <c>OleInitialize</c>の明示呼び出しでも解消しないことを確認済み（おそらくセキュリティ製品/
/// グループポリシー等の環境要因でカスタムCOMインターフェースへのQueryInterfaceが妨げられている）。
///
/// 一方、同一環境で <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>
/// （レガシーな<c>SHFileOperation</c>ベースで、IFileOperationのようなカスタムCOMインターフェースの
/// QueryInterfaceを経由しない）は正常に動作することを確認した。本クラスは、起動時に一度だけ
/// <c>IFileOperation</c>のCOMアクティブ化可否をプローブし、
/// - 利用可能な環境: <see cref="ShellFileOperations"/>（ガイド§4.1の実装、進捗通知・キャンセル対応）をそのまま使用
/// - 利用不可な環境: <c>Microsoft.VisualBasic.FileIO.FileSystem</c>ベースのフォールバックへ自動的に切り替え
/// る。<see cref="ShellFileOperations"/>自体はAI_IMPLEMENTATION_GUIDE.md §4.1のコードのまま変更していない。
///
/// フォールバック側の制約:
/// - 進捗通知（<see cref="FileOperationProgress"/>）は提供されない（<c>progress</c>引数は無視される）。
/// - キャンセルは呼び出し前（未着手）の場合のみ有効。処理開始後は同期APIのため中断できない
///   （<see cref="ShellFileOperations"/>のようなUpdateProgress経由の途中中断はできない）。
/// - Explorerの「元に戻す(Ctrl+Z)」UIへの連携は提供されない（ゴミ箱退避自体は行われる）。
/// </remarks>
public static class SafeFileOperations
{
    private static readonly Lazy<bool> IsIFileOperationAvailable = new(ProbeIFileOperationAvailability);

    /// <summary>
    /// ゴミ箱へ安全に移動する。<see cref="ShellFileOperations.SendToRecycleBinAsync"/>を優先し、
    /// このマシンでIFileOperationのCOMアクティブ化が使えない場合は
    /// <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>ベースのフォールバックへ切り替える。
    /// いずれの経路でも不可逆な物理削除（<see cref="File.Delete(string)"/>等）は行わない。
    /// </summary>
    public static Task<bool> SendToRecycleBinAsync(
        string filePath,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return IsIFileOperationAvailable.Value
            ? ShellFileOperations.SendToRecycleBinAsync(filePath, progress, cancellationToken)
            : SendToRecycleBinFallbackAsync(filePath, cancellationToken);
    }

    /// <summary>
    /// ファイルを安全に移動する（Cross-Volume・ディレクトリ自動作成対応）。
    /// <see cref="ShellFileOperations.MoveFileSafelyAsync"/>を優先し、
    /// このマシンでIFileOperationのCOMアクティブ化が使えない場合は
    /// <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>ベースのフォールバックへ切り替える。
    /// </summary>
    public static Task<bool> MoveFileSafelyAsync(
        string sourcePath,
        string destinationDirectory,
        string? newFileName = null,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return IsIFileOperationAvailable.Value
            ? ShellFileOperations.MoveFileSafelyAsync(sourcePath, destinationDirectory, newFileName, progress, cancellationToken)
            : MoveFileFallbackAsync(sourcePath, destinationDirectory, newFileName, cancellationToken);
    }

    /// <summary>
    /// ファイルを安全にコピーする（Cross-Volume・ディレクトリ自動作成対応）。
    /// <see cref="ShellFileOperations.CopyFileSafelyAsync"/>を優先し、
    /// このマシンでIFileOperationのCOMアクティブ化が使えない場合は
    /// <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>ベースのフォールバックへ切り替える。
    /// </summary>
    public static Task<bool> CopyFileSafelyAsync(
        string sourcePath,
        string destinationDirectory,
        string? newFileName = null,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return IsIFileOperationAvailable.Value
            ? ShellFileOperations.CopyFileSafelyAsync(sourcePath, destinationDirectory, newFileName, progress, cancellationToken)
            : CopyFileFallbackAsync(sourcePath, destinationDirectory, newFileName, cancellationToken);
    }

    /// <summary>
    /// ファイルを安全にリネームする（同一フォルダ内）。
    /// <see cref="ShellFileOperations.RenameFileSafelyAsync"/>を優先し、
    /// このマシンでIFileOperationのCOMアクティブ化が使えない場合は
    /// <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>ベースのフォールバックへ切り替える。
    /// </summary>
    /// <remarks>
    /// Windowsのファイルシステムは大文字小文字を区別しないため、大文字小文字のみが異なる
    /// リネーム（例: "report.txt" → "REPORT.txt"）は、COM <c>IFileOperation</c>・
    /// <c>Microsoft.VisualBasic.FileIO</c>フォールバックのいずれも単純な1回のリネームでは
    /// 「移動先に同名ファイルが既に存在する」と誤認識して失敗する。本メソッドはこのケースを検知し、
    /// 一意な一時ファイル名を経由する2段階リネームで確実に反映する。
    /// </remarks>
    public static async Task<bool> RenameFileSafelyAsync(
        string sourcePath,
        string newFileName,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(sourcePath);
        string currentFileName = Path.GetFileName(sourcePath);

        bool isCaseOnlyChange = !string.IsNullOrEmpty(directory)
            && string.Equals(currentFileName, newFileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(currentFileName, newFileName, StringComparison.Ordinal);

        if (!isCaseOnlyChange)
        {
            return await RenameFileSafelyCoreAsync(sourcePath, newFileName, progress, cancellationToken).ConfigureAwait(false);
        }

        string tempFileName = $"{currentFileName}.{Guid.NewGuid():N}.tmp";
        if (!await RenameFileSafelyCoreAsync(sourcePath, tempFileName, progress, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        string tempPath = Path.Combine(directory!, tempFileName);
        return await RenameFileSafelyCoreAsync(tempPath, newFileName, progress, cancellationToken).ConfigureAwait(false);
    }

    private static Task<bool> RenameFileSafelyCoreAsync(
        string sourcePath,
        string newFileName,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return IsIFileOperationAvailable.Value
            ? ShellFileOperations.RenameFileSafelyAsync(sourcePath, newFileName, progress, cancellationToken)
            : RenameFileFallbackAsync(sourcePath, newFileName, cancellationToken);
    }

    /// <summary>このプロセスで<c>IFileOperation</c>のCOMアクティブ化が可能かどうか（診断用に公開）。</summary>
    public static bool IsUsingShellFileOperations => IsIFileOperationAvailable.Value;

    private static Task<bool> SendToRecycleBinFallbackAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            bool isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                return false;
            }

            try
            {
                if (isDirectory)
                {
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                else
                {
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private static Task<bool> MoveFileFallbackAsync(
        string sourcePath,
        string destinationDirectory,
        string? newFileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            string destinationFileName = newFileName ?? Path.GetFileName(sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, destinationFileName);

            try
            {
                // Microsoft.VisualBasic.FileIO.FileSystem.MoveFile はCross-Volume移動にも対応
                // （内部でSHFileOperationのFO_MOVEを使用、必要に応じてコピー+削除に自動フォールバック）。
                FileSystem.MoveFile(sourcePath, destinationPath, UIOption.OnlyErrorDialogs);
                return true;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private static Task<bool> CopyFileFallbackAsync(
        string sourcePath,
        string destinationDirectory,
        string? newFileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            string destinationFileName = newFileName ?? Path.GetFileName(sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, destinationFileName);

            try
            {
                // Microsoft.VisualBasic.FileIO.FileSystem.CopyFile はCross-Volumeコピーにも対応。
                FileSystem.CopyFile(sourcePath, destinationPath, UIOption.OnlyErrorDialogs);
                return true;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private static Task<bool> RenameFileFallbackAsync(
        string sourcePath,
        string newFileName,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            try
            {
                FileSystem.RenameFile(sourcePath, newFileName);
                return true;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// <c>(IFileOperation)new FileOperation()</c>と同一のCOMアクティブ化＋QueryInterfaceを
    /// STAスレッド上で一度だけ試行し、成功可否をキャッシュする。例外は投げない。
    /// </summary>
    private static bool ProbeIFileOperationAvailability()
    {
        bool available = false;

        var thread = new Thread(() =>
        {
            try
            {
                var probe = (IFileOperationProbe)new FileOperationCoClassProbe();
                Marshal.ReleaseComObject(probe);
                available = true;
            }
            catch
            {
                available = false;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return available;
    }

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
