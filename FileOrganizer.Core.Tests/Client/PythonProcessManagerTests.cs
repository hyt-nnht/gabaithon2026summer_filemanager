using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using FileOrganizer.Core.Client;
using FileOrganizer.Core.Win32;
using FileOrganizer.Shared.Models;

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

    // --- SLM事前配置モデル統合（仕様書§3.1「DL待ち時間ゼロ化」）を伴うStartAsyncオーバーロード ---

    [Fact]
    public async Task StartAsync_統合版_事前配置モデルがReadyならダウンロードせず即起動する()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55126"]);

        // HTTP通信が発生したら即座に失敗させ、「ダウンロードが一切行われない」ことを検証する。
        using var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException(
            "事前配置モデルがReadyの場合、オンデマンドダウンロードは発生しないはず。"));
        using var httpClient = new HttpClient(handler);
        using var modelDownloadManager = new ModelDownloadManager(httpClient);

        string modelPath = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "PythonProcessManager", $"{Guid.NewGuid():N}.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        using (var fs = new FileStream(modelPath, FileMode.Create))
        {
            fs.SetLength(ModelDownloadManager.ExpectedModelSizeBytes);
        }

        try
        {
            var settings = new AppSettings { UsePreloadedSlmModel = true, SlmModelPath = modelPath };
            var progressValues = new List<double>();
            var progress = new SyncProgress<double>(v => progressValues.Add(v));

            PythonHandshakeResult result = await manager.StartAsync(settings, modelDownloadManager, progress);

            Assert.Equal(55126, result.Port);
            Assert.Contains(1.0, progressValues); // 即完了として1.0が通知される。
        }
        finally
        {
            try { File.Delete(modelPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task StartAsync_統合版_事前配置モデルが無ければダウンロードしてから起動する()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55127"]);

        long size = (long)(ModelDownloadManager.ExpectedModelSizeBytes * 0.6);
        byte[] content = new byte[size];
        using var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.OkResponse(content));
        using var httpClient = new HttpClient(handler);
        using var modelDownloadManager = new ModelDownloadManager(httpClient);

        string modelPath = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "PythonProcessManager", $"{Guid.NewGuid():N}.gguf");

        try
        {
            var settings = new AppSettings { UsePreloadedSlmModel = true, SlmModelPath = modelPath };
            var progressValues = new List<double>();
            var progress = new SyncProgress<double>(v => progressValues.Add(v));

            PythonHandshakeResult result = await manager.StartAsync(settings, modelDownloadManager, progress);

            Assert.Equal(55127, result.Port);
            Assert.True(File.Exists(modelPath), "ダウンロードされたモデルファイルが存在すること。");
            Assert.Contains(0.0, progressValues);
            Assert.Contains(1.0, progressValues);
        }
        finally
        {
            try { File.Delete(modelPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task StartAsync_統合版_ダウンロード失敗時はInvalidOperationExceptionを投げPythonを起動しない()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55128"]);

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        using var modelDownloadManager = new ModelDownloadManager(httpClient);

        string modelPath = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "PythonProcessManager", $"{Guid.NewGuid():N}.gguf");
        var settings = new AppSettings { UsePreloadedSlmModel = true, SlmModelPath = modelPath };

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(settings, modelDownloadManager));

        Assert.Null(manager.Process); // Pythonプロセスは起動されていないこと。
    }

    // --- 異常終了検知（仕様書§7.2-3: Process.Exited + JobObject監視の組み合わせ） ---

    [Fact]
    public async Task ProcessCrashed_ハンドシェイク完了後にプロセスが終了すると発火する()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs:
            ["-Port", "55140", "-CrashAfterHandshakeMs", "200", "-CrashExitCode", "9"]);

        var crashedTcs = new TaskCompletionSource<PythonProcessCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProcessCrashed += (_, e) => crashedTcs.TrySetResult(e);

        PythonHandshakeResult result = await manager.StartAsync();
        Assert.Equal(55140, result.Port);

        PythonProcessCrashedEventArgs crashed = await crashedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(9, crashed.ExitCode);
        Assert.Equal(manager.Process!.Id, crashed.ProcessId);
    }

    [Fact]
    public async Task ProcessCrashed_Disposeによる意図的な停止では発火しない()
    {
        using var jobObjectManager = new JobObjectManager();
        var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55141"]);

        bool crashedFired = false;
        manager.ProcessCrashed += (_, _) => crashedFired = true;

        await manager.StartAsync();
        manager.Dispose();

        // Killによる終了イベント伝播を待つため少し猶予を持たせる。
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(crashedFired, "Dispose()による意図的な停止でProcessCrashedが発火してはならない。");
    }

    [Fact]
    public async Task StartAsync_統合版_SLM機能無効時はダウンロードせず起動する()
    {
        using var jobObjectManager = new JobObjectManager();
        using var manager = CreateManager(jobObjectManager, extraArgs: ["-Port", "55129"]);

        using var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException(
            "UsePreloadedSlmModelがfalseの場合、オンデマンドダウンロードは発生しないはず。"));
        using var httpClient = new HttpClient(handler);
        using var modelDownloadManager = new ModelDownloadManager(httpClient);

        var settings = new AppSettings { UsePreloadedSlmModel = false, SlmModelPath = "" };

        PythonHandshakeResult result = await manager.StartAsync(settings, modelDownloadManager);

        Assert.Equal(55129, result.Port);
    }
}
