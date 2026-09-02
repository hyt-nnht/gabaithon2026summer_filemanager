using System.Collections.Concurrent;
using FileOrganizer.Core.Client;
using FileOrganizer.Core.Win32;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Client;

/// <summary>
/// <see cref="PythonServiceSupervisor"/> の単体テスト。
/// 仕様書§7.2-3「推論中のOOM等でPythonプロセスが異常終了した場合、自動で1回リスポーンして
/// 自己復旧すること」を、実際の子プロセス起動（<c>mock_py_service.ps1</c>）とスタブ化した
/// <see cref="IPythonApiClient"/>（<see cref="FakePythonApiClient"/>）の組み合わせで検証する。
/// </summary>
public class PythonServiceSupervisorTests
{
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "mock_py_service.ps1");

    private static PythonProcessManager CreateManager(JobObjectManager jobObjectManager, params string[] extraArgs)
    {
        string[] arguments =
        [
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", ScriptPath,
            .. extraArgs,
        ];
        return new PythonProcessManager(jobObjectManager, "powershell.exe", arguments);
    }

    [Fact]
    public async Task HealthCheckAsync_初回成功なら_リスポーンせず結果をそのまま返す()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        apiClient.EnqueueHealthCheckResult(true);

        await using var supervisor = new PythonServiceSupervisor(
            () => CreateManager(jobObjectManager, "-Port", "55160"), apiClient);

        bool degradedFired = false;
        supervisor.ServiceDegraded += (_, _) => degradedFired = true;

        await supervisor.StartAsync();
        bool result = await supervisor.HealthCheckAsync();

        Assert.True(result);
        Assert.False(degradedFired);
        Assert.Equal(0, supervisor.ConsecutiveFailureCount);
        Assert.Single(apiClient.ConfigureCalls); // 初回起動時の1回のみ。リスポーンは発生していない。
    }

    [Fact]
    public async Task HealthCheckAsync_初回失敗でもリスポーン後の再試行が成功すれば自己復旧する()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        apiClient.EnqueueHealthCheckResult(false); // 1回目: 失敗（クラッシュ相当）
        apiClient.EnqueueHealthCheckResult(true);  // リスポーン後の再試行: 成功

        await using var supervisor = new PythonServiceSupervisor(
            () => CreateManager(jobObjectManager, "-Port", "55161"), apiClient);

        bool degradedFired = false;
        supervisor.ServiceDegraded += (_, _) => degradedFired = true;

        await supervisor.StartAsync();

        bool result = await supervisor.HealthCheckAsync();

        Assert.True(result); // 1回のリスポーン＋再試行で自己復旧できていること。
        Assert.False(degradedFired, "自己復旧できた場合はServiceDegradedを発火してはならない。");
        Assert.Equal(0, supervisor.ConsecutiveFailureCount);
        Assert.Equal(2, apiClient.ConfigureCalls.Count); // 初回起動 + リスポーン1回。
        // mock_py_serviceは固定ポートをそのまま出力するためPortでは判定できないが、
        // Tokenは起動のたびに新規生成されるため、リスポーンで再ハンドシェイクされたことを確認できる。
        Assert.NotEqual(apiClient.ConfigureCalls[0].Token, apiClient.ConfigureCalls[1].Token);
    }

    [Fact]
    public async Task HealthCheckAsync_統合起動後のリスポーンでもSlmModel環境変数を引き継ぐ()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        apiClient.EnqueueHealthCheckResult(false);
        apiClient.EnqueueHealthCheckResult(true);

        string directory = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "PythonServiceSupervisor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string modelPath = Path.Combine(directory, "model.gguf");
        string envDumpPath = Path.Combine(directory, "model-path.txt");
        using (var stream = new FileStream(modelPath, FileMode.Create))
        {
            stream.SetLength(ModelDownloadManager.ExpectedModelSizeBytes);
        }

        try
        {
            using var modelDownloadManager = new ModelDownloadManager();
            await using var supervisor = new PythonServiceSupervisor(
                () => CreateManager(jobObjectManager, "-Port", "55167", "-EnvDumpPath", envDumpPath),
                apiClient);
            var settings = new AppSettings { UsePreloadedSlmModel = true, SlmModelPath = modelPath };

            await supervisor.StartAsync(settings, modelDownloadManager);
            Assert.True(await supervisor.HealthCheckAsync());

            Assert.Equal(modelPath, (await File.ReadAllTextAsync(envDumpPath)).Trim());
            Assert.Equal(2, apiClient.ConfigureCalls.Count);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HealthCheckAsync_リスポーン後も失敗が続く場合はServiceDegradedを発火する()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        apiClient.EnqueueHealthCheckResult(false); // 1回目: 失敗
        apiClient.EnqueueHealthCheckResult(false); // リスポーン後の再試行も失敗 = 連続失敗

        await using var supervisor = new PythonServiceSupervisor(
            () => CreateManager(jobObjectManager, "-Port", "55162"), apiClient);

        PythonServiceDegradedEventArgs? degraded = null;
        supervisor.ServiceDegraded += (_, e) => degraded = e;

        await supervisor.StartAsync();
        bool result = await supervisor.HealthCheckAsync();

        Assert.False(result);
        Assert.NotNull(degraded);
        Assert.Equal("HealthCheck", degraded!.OperationName);
        Assert.True(degraded.RespawnAttempted);
        Assert.True(degraded.RespawnSucceeded); // リスポーン自体（プロセス再起動）は成功している。
        Assert.Equal(1, supervisor.ConsecutiveFailureCount);
        Assert.Equal(2, apiClient.ConfigureCalls.Count); // リスポーンは1回だけ行われたこと。
    }

    [Fact]
    public async Task HealthCheckAsync_リスポーン自体が失敗した場合もServiceDegradedを発火する()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        apiClient.EnqueueHealthCheckResult(false); // 初回失敗 → リスポーンを試みるが…

        int callCount = 0;
        Func<PythonProcessManager> factory = () =>
        {
            callCount++;
            return callCount == 1
                ? CreateManager(jobObjectManager, "-Port", "55163") // 初回起動は成功
                : CreateManager(jobObjectManager, "-ExitCode", "3"); // リスポーン時は起動直後に異常終了
        };

        await using var supervisor = new PythonServiceSupervisor(factory, apiClient);

        PythonServiceDegradedEventArgs? degraded = null;
        supervisor.ServiceDegraded += (_, e) => degraded = e;

        await supervisor.StartAsync();
        bool result = await supervisor.HealthCheckAsync();

        Assert.False(result);
        Assert.NotNull(degraded);
        Assert.True(degraded!.RespawnAttempted);
        Assert.False(degraded.RespawnSucceeded); // リスポーン（プロセス再起動）自体が失敗している。
        Assert.NotNull(degraded.RespawnException);
        Assert.Single(apiClient.ConfigureCalls); // Configureは初回起動時のみ（リスポーン失敗時は呼ばれない）。
    }

    [Fact]
    public async Task HealthCheckAsync_同時多発の失敗でもリスポーンは1回だけ行われる()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        // 2つの同時呼び出しがどちらも初回失敗 → どちらもリスポーンを試みるが、実際の起動は1回のみのはず。
        apiClient.EnqueueHealthCheckResult(false);
        apiClient.EnqueueHealthCheckResult(false);
        apiClient.EnqueueHealthCheckResult(true);
        apiClient.EnqueueHealthCheckResult(true);

        await using var supervisor = new PythonServiceSupervisor(
            () => CreateManager(jobObjectManager, "-Port", "55164"), apiClient);

        await supervisor.StartAsync();

        Task<bool> call1 = supervisor.HealthCheckAsync();
        Task<bool> call2 = supervisor.HealthCheckAsync();
        bool[] results = await Task.WhenAll(call1, call2);

        Assert.All(results, Assert.True);
        Assert.Equal(2, apiClient.ConfigureCalls.Count); // 初回起動 + リスポーン「1回」のみ（2回にならない）。
    }

    [Fact]
    public async Task HealthCheckAsync_Start前に呼ぶとInvalidOperationExceptionを投げる()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();

        await using var supervisor = new PythonServiceSupervisor(
            () => CreateManager(jobObjectManager, "-Port", "55165"), apiClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.HealthCheckAsync());
    }

    [Fact]
    public async Task ProcessCrashed_PythonProcessManagerのイベントがSupervisor経由で中継される()
    {
        using var jobObjectManager = new JobObjectManager();
        var apiClient = new FakePythonApiClient();
        apiClient.EnqueueHealthCheckResult(true);

        await using var supervisor = new PythonServiceSupervisor(
            () => CreateManager(jobObjectManager, "-Port", "55166", "-CrashAfterHandshakeMs", "200", "-CrashExitCode", "7"),
            apiClient);

        var crashedTcs = new TaskCompletionSource<PythonProcessCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.ProcessCrashed += (_, e) => crashedTcs.TrySetResult(e);

        await supervisor.StartAsync();

        PythonProcessCrashedEventArgs crashed = await crashedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, crashed.ExitCode);
    }

    /// <summary>
    /// テスト用の<see cref="IPythonApiClient"/>スタブ。<see cref="HealthCheckAsync"/>の戻り値を
    /// キューで差し替えられるようにし、<see cref="Configure"/>の呼び出し履歴（port/token）を記録する。
    /// </summary>
    private sealed class FakePythonApiClient : IPythonApiClient
    {
        private readonly ConcurrentQueue<bool> _healthCheckResults = new();

        public List<(int Port, string Token)> ConfigureCalls { get; } = [];

        public void EnqueueHealthCheckResult(bool result) => _healthCheckResults.Enqueue(result);

        public void Configure(int port, string bearerToken)
        {
            lock (ConfigureCalls)
            {
                ConfigureCalls.Add((port, bearerToken));
            }
        }

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
            Task.FromResult(_healthCheckResults.TryDequeue(out bool result) ? result : true);

        public Task<AnalyzeResponse?> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("このテストではHealthCheckAsyncのみを使用する。");
    }
}
