using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileOrganizer.Core.Win32;

/// <summary>
/// Windows Job Object を用いて、C#アプリ終了時にPython子プロセス（およびその配下）を
/// 確実に道連れ終了させるためのラッパー。
/// 仕様書§7.2-3「C#アプリ終了時、Python子プロセスが確実に道連れ終了すること」の実装基盤。
/// AI_IMPLEMENTATION_GUIDE.md §4.2 をベースに、エラーハンドリングとIDisposable実装を強化。
/// </summary>
public sealed class JobObjectManager : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectBasicProcessIdList = 3;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    /// <summary>
    /// <see cref="GetActiveProcessIds"/>が一度に取得できるPID件数の上限。
    /// Python本体（+稀に孫プロセス）程度を想定した実用上の上限であり、通常の運用では十分な余裕がある。
    /// </summary>
    private const int MaxTrackedProcessIds = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryLimit, PeakJobMemoryLimit;
    }

    private IntPtr _jobHandle;
    private bool _disposed;

    public JobObjectManager()
    {
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject に失敗しました。");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr pInfo = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pInfo, false);
            if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, pInfo, (uint)length))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
                throw new Win32Exception(error, "SetInformationJobObject（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE設定）に失敗しました。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pInfo);
        }
    }

    /// <summary>
    /// 指定プロセスをこのJob Objectに割り当てる。
    /// 割り当てに失敗した場合（対象プロセスが既に別のJob Objectに所属している等）、
    /// GetLastError由来のWin32Exceptionをスローする。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Dispose済みの場合。</exception>
    /// <exception cref="Win32Exception">AssignProcessToJobObject がネイティブ側で失敗した場合。</exception>
    public void AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!AssignProcessToJobObject(_jobHandle, process.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"AssignProcessToJobObject に失敗しました（PID={process.Id}）。");
        }
    }

    /// <summary>
    /// このJob Objectに現在割り当てられている（＝まだ生存している）プロセスのPID一覧を取得する。
    /// 仕様書§7.2-3のクラッシュ検知（<see cref="PythonProcessManager"/>の
    /// <c>Process.Exited</c>イベント）を、OS側の実際のジョブ所属状況と突き合わせて誤検知を避けるために使う。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Dispose済みの場合。</exception>
    /// <exception cref="Win32Exception">
    /// QueryInformationJobObjectがネイティブ側で失敗した場合（<see cref="MaxTrackedProcessIds"/>を超える
    /// プロセス数が割り当てられている場合を含む）。
    /// </exception>
    public IReadOnlyList<int> GetActiveProcessIds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int headerSize = sizeof(uint) * 2; // NumberOfAssignedProcesses + NumberOfProcessIdsInList
        int bufferSize = headerSize + (IntPtr.Size * MaxTrackedProcessIds);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!QueryInformationJobObject(_jobHandle, JobObjectBasicProcessIdList, buffer, (uint)bufferSize, out _))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "QueryInformationJobObject（JobObjectBasicProcessIdList取得）に失敗しました。");
            }

            int numberOfProcessIdsInList = Marshal.ReadInt32(buffer, sizeof(uint));
            var result = new List<int>(numberOfProcessIdsInList);
            for (int i = 0; i < numberOfProcessIdsInList; i++)
            {
                IntPtr pid = Marshal.ReadIntPtr(buffer, headerSize + (i * IntPtr.Size));
                result.Add((int)pid.ToInt64());
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>指定PIDが現在もこのJob Objectに割り当てられている（＝生存している）かどうか。</summary>
    /// <exception cref="ObjectDisposedException">Dispose済みの場合。</exception>
    public bool IsProcessActive(int processId) => GetActiveProcessIds().Contains(processId);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (_jobHandle != IntPtr.Zero)
        {
            // Job Object ハンドルを閉じることで、JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE により
            // 割り当て済みの全プロセス（Python子プロセス含む）が道連れ終了する。
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        _disposed = true;
    }

    ~JobObjectManager()
    {
        Dispose(false);
    }
}
