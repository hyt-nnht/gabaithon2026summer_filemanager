using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace FileOrganizer.Core.Win32;

public record FileOperationProgress(
    uint TotalItems,
    uint ItemsProcessed,
    uint Percentage,
    string CurrentItemPath
);

public static class ShellFileOperations
{
    [Flags]
    public enum FileOperationFlags : uint
    {
        FOF_SILENT = 0x0004,
        FOF_NOCONFIRMATION = 0x0010,
        FOF_ALLOWUNDO = 0x0040,
        FOF_NOERRORUI = 0x0400,
        FOF_WANTNUKEWARNING = 0x4000
    }

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOperation { }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink pfops);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(FileOperationFlags dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
        void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IFileOperationProgressSink? pfopsItem);
        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IFileOperationProgressSink? pfopsItem);
        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IFileOperationProgressSink? pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("001076ee-ee50-470e-ac5e-7d3c5949cb4c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        void StartOperations();
        void FinishOperations(int hrResult);
        void PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrRename, IShellItem psiNewlyCreated);
        void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrMove, IShellItem psiNewlyCreated);
        void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrCopy, IShellItem psiNewlyCreated);
        void PreDeleteItem(uint dwFlags, IShellItem psiItem);
        void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated);
        void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem);
        void UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        void ResetTimer();
        void PauseTimer();
        void ResumeTimer();
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        IntPtr BindToHandler(IBindCtx pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
        IntPtr GetParent();
        IntPtr GetDisplayName(uint sigdnName);
        uint GetAttributes(uint sfgaoMask);
        int Compare(IShellItem psi, uint hint);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private static readonly Guid IShellItemGuid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // --- 固定長 STA スレッドプール管理 ---
    private static readonly BlockingCollection<Action> StaTaskQueue = new();
    private const int WorkerCount = 4;

    static ShellFileOperations()
    {
        for (int i = 0; i < WorkerCount; i++)
        {
            var workerThread = new Thread(ProcessStaQueue)
            {
                IsBackground = true,
                Name = $"ShellFileOperations_STA_Worker_{i}"
            };
            workerThread.SetApartmentState(ApartmentState.STA);
            workerThread.Start();
        }
    }

    private static void ProcessStaQueue()
    {
        foreach (var action in StaTaskQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // 各タスクの TaskCompletionSource 側でハンドル
            }
        }
    }

    private static Task<T> EnqueueStaTaskAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            tcs.SetCanceled(cancellationToken);
            return tcs.Task;
        }

        StaTaskQueue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.SetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.SetResult(func());
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private class FileOpProgressSink : IFileOperationProgressSink
    {
        private readonly IProgress<FileOperationProgress>? _progress;
        private readonly CancellationToken _cancellationToken;
        private string _currentPath = string.Empty;

        public FileOpProgressSink(IProgress<FileOperationProgress>? progress, CancellationToken cancellationToken)
        {
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        public void StartOperations() { }
        public void FinishOperations(int hrResult) { }

        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => SetCurrentPath(psiItem);
        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrMove, IShellItem psiNewlyCreated) { }

        public void PreDeleteItem(uint dwFlags, IShellItem psiItem) => SetCurrentPath(psiItem);
        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated) { }

        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => SetCurrentPath(psiItem);
        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrCopy, IShellItem psiNewlyCreated) { }

        public void PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => SetCurrentPath(psiItem);
        public void PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem psiNewlyCreated) { }

        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) { }
        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem) { }

        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
            // CCW変換によりネイティブ側へ E_ABORT を返し、シェル操作を中断させる
            _cancellationToken.ThrowIfCancellationRequested();

            if (_progress == null || iWorkTotal == 0) return;
            uint percentage = (uint)((double)iWorkSoFar / iWorkTotal * 100);
            _progress.Report(new FileOperationProgress(iWorkTotal, iWorkSoFar, percentage, _currentPath));
        }

        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }

        private void SetCurrentPath(IShellItem item)
        {
            try
            {
                IntPtr pStr = item.GetDisplayName(0x80058000); // SIGDN_FILESYSPATH
                if (pStr != IntPtr.Zero)
                {
                    _currentPath = Marshal.PtrToStringUni(pStr) ?? string.Empty;
                    Marshal.FreeCoTaskMem(pStr);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// ゴミ箱へ安全に移動（非同期・固定STAプール・キャンセル・進捗通知対応）
    /// </summary>
    public static Task<bool> SendToRecycleBinAsync(
        string filePath,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return EnqueueStaTaskAsync(() =>
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath)) return false;

            IFileOperation? fileOp = null;
            uint cookie = 0;
            try
            {
                fileOp = (IFileOperation)new FileOperation();
                fileOp.SetOperationFlags(FileOperationFlags.FOF_ALLOWUNDO | FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_SILENT | FileOperationFlags.FOF_NOERRORUI);

                var sink = new FileOpProgressSink(progress, cancellationToken);
                cookie = fileOp.Advise(sink);

                SHCreateItemFromParsingName(filePath, IntPtr.Zero, IShellItemGuid, out IShellItem item);
                fileOp.DeleteItem(item, sink);
                fileOp.PerformOperations();

                // 【重要】コールバック中断または外部キャンセル要求を検知し、Task に確実に Canceled を伝播
                cancellationToken.ThrowIfCancellationRequested();

                return !fileOp.GetAnyOperationsAborted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fileOp != null)
                {
                    if (cookie != 0) fileOp.Unadvise(cookie);
                    Marshal.ReleaseComObject(fileOp);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// ファイルを安全に移動（非同期・固定STAプール・Cross-Volume並行処理・キャンセル・進捗通知・ディレクトリ自動生成）
    /// </summary>
    public static Task<bool> MoveFileSafelyAsync(
        string sourcePath,
        string destinationDirectory,
        string? newFileName = null,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return EnqueueStaTaskAsync(() =>
        {
            if (!File.Exists(sourcePath)) return false;

            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            IFileOperation? fileOp = null;
            uint cookie = 0;
            try
            {
                fileOp = (IFileOperation)new FileOperation();
                fileOp.SetOperationFlags(FileOperationFlags.FOF_ALLOWUNDO | FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_SILENT | FileOperationFlags.FOF_NOERRORUI);

                var sink = new FileOpProgressSink(progress, cancellationToken);
                cookie = fileOp.Advise(sink);

                SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, IShellItemGuid, out IShellItem sourceItem);
                SHCreateItemFromParsingName(destinationDirectory, IntPtr.Zero, IShellItemGuid, out IShellItem destFolderItem);

                fileOp.MoveItem(sourceItem, destFolderItem, newFileName, sink);
                fileOp.PerformOperations();

                // 【重要】コールバック中断または外部キャンセル要求を検知し、Task に確実に Canceled を伝播
                cancellationToken.ThrowIfCancellationRequested();

                return !fileOp.GetAnyOperationsAborted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fileOp != null)
                {
                    if (cookie != 0) fileOp.Unadvise(cookie);
                    Marshal.ReleaseComObject(fileOp);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// ファイルを安全にコピー（非同期・固定STAプール・Cross-Volume並行処理・キャンセル・進捗通知・ディレクトリ自動生成）
    /// </summary>
    public static Task<bool> CopyFileSafelyAsync(
        string sourcePath,
        string destinationDirectory,
        string? newFileName = null,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return EnqueueStaTaskAsync(() =>
        {
            if (!File.Exists(sourcePath)) return false;

            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            IFileOperation? fileOp = null;
            uint cookie = 0;
            try
            {
                fileOp = (IFileOperation)new FileOperation();
                fileOp.SetOperationFlags(FileOperationFlags.FOF_ALLOWUNDO | FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_SILENT | FileOperationFlags.FOF_NOERRORUI);

                var sink = new FileOpProgressSink(progress, cancellationToken);
                cookie = fileOp.Advise(sink);

                SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, IShellItemGuid, out IShellItem sourceItem);
                SHCreateItemFromParsingName(destinationDirectory, IntPtr.Zero, IShellItemGuid, out IShellItem destFolderItem);

                fileOp.CopyItem(sourceItem, destFolderItem, newFileName, sink);
                fileOp.PerformOperations();

                // 【重要】コールバック中断または外部キャンセル要求を検知し、Task に確実に Canceled を伝播
                cancellationToken.ThrowIfCancellationRequested();

                return !fileOp.GetAnyOperationsAborted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fileOp != null)
                {
                    if (cookie != 0) fileOp.Unadvise(cookie);
                    Marshal.ReleaseComObject(fileOp);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// ファイルを安全にリネーム（同一フォルダ内、非同期・固定STAプール・キャンセル・進捗通知対応）
    /// </summary>
    public static Task<bool> RenameFileSafelyAsync(
        string sourcePath,
        string newFileName,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return EnqueueStaTaskAsync(() =>
        {
            if (!File.Exists(sourcePath)) return false;

            IFileOperation? fileOp = null;
            uint cookie = 0;
            try
            {
                fileOp = (IFileOperation)new FileOperation();
                fileOp.SetOperationFlags(FileOperationFlags.FOF_ALLOWUNDO | FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_SILENT | FileOperationFlags.FOF_NOERRORUI);

                var sink = new FileOpProgressSink(progress, cancellationToken);
                cookie = fileOp.Advise(sink);

                SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, IShellItemGuid, out IShellItem sourceItem);

                fileOp.RenameItem(sourceItem, newFileName, sink);
                fileOp.PerformOperations();

                cancellationToken.ThrowIfCancellationRequested();

                return !fileOp.GetAnyOperationsAborted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fileOp != null)
                {
                    if (cookie != 0) fileOp.Unadvise(cookie);
                    Marshal.ReleaseComObject(fileOp);
                }
            }
        }, cancellationToken);
    }
}
