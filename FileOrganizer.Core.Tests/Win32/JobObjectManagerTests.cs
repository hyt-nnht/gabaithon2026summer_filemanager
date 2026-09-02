using System.Diagnostics;
using FileOrganizer.Core.Win32;

namespace FileOrganizer.Core.Tests.Win32;

/// <summary>
/// 仕様書§7.2-3「C#アプリ終了時、Python子プロセスが確実に道連れ終了すること」の検証土台。
/// 本番のPython子プロセスの代わりに notepad.exe をダミー子プロセスとして使用する。
/// </summary>
public class JobObjectManagerTests
{
    /// <summary>
    /// PATH上の "notepad.exe" は Windows 11 のApp Execution Alias（WindowsApps配下のスタブ）を
    /// 経由し、即座に終了する別プロセスへ委譲される場合がある。
    /// System32配下を直接指定することで、実プロセスを確実に子プロセスとして起動する。
    /// </summary>
    private static string NotepadPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    [Fact]
    public void Dispose_KillsAssignedProcess()
    {
        using var process = Process.Start(new ProcessStartInfo(NotepadPath)
        {
            UseShellExecute = false,
        });
        Assert.NotNull(process);

        try
        {
            var jobObjectManager = new JobObjectManager();
            jobObjectManager.AssignProcess(process);

            Assert.False(process.HasExited);

            // Job Object破棄 = C#アプリ終了時相当。KILL_ON_JOB_CLOSEにより道連れ終了するはず。
            jobObjectManager.Dispose();

            // プロセス終了はOS側で非同期に行われるため、短いポーリングで待機する。
            bool exited = process.WaitForExit(TimeSpan.FromSeconds(5));
            Assert.True(exited, "JobObjectManager破棄後、割り当てたプロセスが道連れ終了しませんでした。");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [Fact]
    public void AssignProcess_AfterDispose_ThrowsObjectDisposedException()
    {
        var jobObjectManager = new JobObjectManager();
        jobObjectManager.Dispose();

        using var process = Process.Start(new ProcessStartInfo(NotepadPath)
        {
            UseShellExecute = false,
        });
        Assert.NotNull(process);

        try
        {
            Assert.Throws<ObjectDisposedException>(() => jobObjectManager.AssignProcess(process));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [Fact]
    public void AssignProcess_NullProcess_ThrowsArgumentNullException()
    {
        using var jobObjectManager = new JobObjectManager();

        Assert.Throws<ArgumentNullException>(() => jobObjectManager.AssignProcess(null!));
    }

    // --- GetActiveProcessIds/IsProcessActive: PythonProcessManagerの異常終了検知（仕様書§7.2-3）が
    //     Process.Exitedと突き合わせる「Job Object側の実プロセス一覧」の検証。 ---

    [Fact]
    public void GetActiveProcessIds_AfterAssign_ContainsAssignedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo(NotepadPath) { UseShellExecute = false });
        Assert.NotNull(process);

        try
        {
            using var jobObjectManager = new JobObjectManager();
            jobObjectManager.AssignProcess(process);

            Assert.Contains(process.Id, jobObjectManager.GetActiveProcessIds());
            Assert.True(jobObjectManager.IsProcessActive(process.Id));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [Fact]
    public void IsProcessActive_AfterProcessExits_ReturnsFalse()
    {
        using var process = Process.Start(new ProcessStartInfo(NotepadPath) { UseShellExecute = false });
        Assert.NotNull(process);

        using var jobObjectManager = new JobObjectManager();
        jobObjectManager.AssignProcess(process);
        Assert.True(jobObjectManager.IsProcessActive(process.Id));

        process.Kill();
        process.WaitForExit(TimeSpan.FromSeconds(5));

        Assert.False(jobObjectManager.IsProcessActive(process.Id));
    }

    [Fact]
    public void GetActiveProcessIds_AfterDispose_ThrowsObjectDisposedException()
    {
        var jobObjectManager = new JobObjectManager();
        jobObjectManager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => jobObjectManager.GetActiveProcessIds());
    }
}
