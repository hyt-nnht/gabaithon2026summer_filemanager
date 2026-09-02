using System.Diagnostics;
using System.Text.RegularExpressions;
using FileOrganizer.Core.Client;
using FileOrganizer.Core.Win32;

namespace FileOrganizer.Core.Tests.Client;

/// <summary>
/// 仕様書§7.2-5「Port 0動的割当およびBearer Token認証」の起動側実装の検証。
/// 実際のpy_serviceの代わりに <c>TestAssets/mock_py_service.ps1</c>（PowerShell）を
/// ダミーPythonプロセスとして使用する。
/// </summary>
public class PythonProcessManagerTests
{
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "mock_py_service.ps1");
    private static readonly Regex HexToken = new("^[0-9A-Fa-f]{32}$", RegexOptions.Compiled);

    private static PythonProcessManager CreateManager(
        JobObjectManager jobObjectManager,
        TimeSpan? handshakeTimeout = null,
        params string[] extraArgs)
    {
        string[] arguments =
        [
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", ScriptPath,
            .. extraArgs,
        ];
        return new PythonProcessManager(jobObjectManager, "powershell.exe", arguments, handshakeTimeout: handshakeTimeout);
    }

    [Fact]
    public async Task StartAsync_ParsesPortLine_AndReturnsHandshakeResult()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55123"]);

        PythonHandshakeResult result = await manager.StartAsync();

        Assert.Equal(55123, result.Port);
        Assert.Equal(new Uri("http://127.0.0.1:55123"), result.BaseUri);
        Assert.Matches(HexToken, result.Token);
        Assert.False(manager.Process!.HasExited);
    }

    [Fact]
    public async Task StartAsync_AssignsProcessToJobObject_SoDisposingJobKillsIt()
    {
        var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55124"]);

        await manager.StartAsync();
        Process process = manager.Process!;
        Assert.False(process.HasExited);

        // JobObjectManager破棄 = C#アプリ終了時相当。KILL_ON_JOB_CLOSEにより道連れ終了するはず。
        jobObjectManager.Dispose();

        bool exited = process.WaitForExit(TimeSpan.FromSeconds(5));
        Assert.True(exited, "JobObjectManager破棄後、起動したPythonプロセスが道連れ終了しませんでした。");
    }

    [Fact]
    public async Task StartAsync_NoPortLineWithinTimeout_ThrowsTimeoutExceptionAndKillsProcess()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, handshakeTimeout: TimeSpan.FromSeconds(1), extraArgs: ["-SuppressPort"]);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => manager.StartAsync());
        Assert.Contains("秒", ex.Message);

        // タイムアウト時はプロセスを後始末していること。
        Process process = manager.Process!;
        bool exited = process.WaitForExit(TimeSpan.FromSeconds(5));
        Assert.True(exited, "タイムアウト後もPythonプロセスが終了していません。");
    }

    [Fact]
    public async Task StartAsync_ProcessExitsBeforeHandshake_ThrowsInvalidOperationExceptionWithExitCode()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-ExitCode", "3"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());
        Assert.Contains("ExitCode=3", ex.Message);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55125"]);

        await manager.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());
    }
}
