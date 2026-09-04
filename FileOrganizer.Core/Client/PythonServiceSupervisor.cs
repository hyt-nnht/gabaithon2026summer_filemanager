using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Client;

/// <summary>
/// <see cref="PythonServiceSupervisor.ServiceDegraded"/>で通知される「連続失敗」情報。
/// 元のAPI呼び出し失敗に対して自動リスポーン＋再試行（<see cref="PythonServiceSupervisor"/>）を行っても
/// なお復旧できなかった場合に発火する。UI層はこれを購読して、ユーザーへの警告表示・SLM機能の一時無効化等に使う。
/// </summary>
public sealed class PythonServiceDegradedEventArgs : EventArgs
{
    /// <summary>失敗した操作名（診断用。例: "HealthCheck", "Analyze"）。</summary>
    public required string OperationName { get; init; }

    /// <summary>
    /// 自動リスポーン自体を試みたかどうか。<c>false</c>の場合、直前の<see cref="PythonProcessManager.ProcessCrashed"/>等
    /// 検知に依らず「リスポーン処理自体の起動に失敗した」ことを意味する（プロセス起動不可等）。
    /// </summary>
    public required bool RespawnAttempted { get; init; }

    /// <summary>リスポーン自体が成功したかどうか（<see cref="RespawnAttempted"/>が<c>false</c>の場合は常に<c>false</c>）。</summary>
    public required bool RespawnSucceeded { get; init; }

    /// <summary>リスポーン試行中に発生した例外（発生しなかった場合は<c>null</c>）。</summary>
    public Exception? RespawnException { get; init; }
}

/// <summary>Python分類リクエストの開始・完了をUIへ通知するための状態。</summary>
public sealed class PythonAnalysisStateChangedEventArgs : EventArgs
{
    public required bool IsRunning { get; init; }
    public AnalyzeResponse? Response { get; init; }
    public Exception? Error { get; init; }
}

/// <summary>
/// <see cref="PythonProcessManager"/>（プロセス起動・クラッシュ検知）と<see cref="IPythonApiClient"/>
/// （HTTP呼び出し）を束ね、仕様書§7.2-3「推論中のOOM等でPythonプロセスが異常終了した場合、
/// 自動で1回リスポーンして自己復旧すること」を実現するオーケストレーター。
/// </summary>
/// <remarks>
/// <para>
/// <b>復旧フロー</b>: <see cref="HealthCheckAsync"/>/<see cref="AnalyzeAsync"/>経由のAPI呼び出しが
/// 失敗（<c>false</c>/<c>null</c>）した場合、以下を1回だけ試みる。
/// </para>
/// <list type="number">
/// <item><description>現在の<see cref="PythonProcessManager"/>を破棄（未終了なら道連れKill）。</description></item>
/// <item><description>新しい<see cref="PythonProcessManager"/>を起動し、ハンドシェイク（Port 0確定 + Token再生成）をやり直す。</description></item>
/// <item><description>新しいPort/Tokenで<see cref="IPythonApiClient.Configure"/>を呼び直す。</description></item>
/// <item><description>元のAPI呼び出しを1回だけ再試行する。</description></item>
/// </list>
/// <para>
/// リスポーン自体の失敗、またはリスポーン後の再試行も失敗した場合（＝連続失敗）は<see cref="ServiceDegraded"/>を
/// 発火してUI層へ通知する。同時に複数の呼び出しが失敗した場合でも、リスポーンは1回のみ実行され
/// （<see cref="_respawnLock"/>で直列化し、既に他呼び出しがリスポーン済みなら二重リスポーンを回避）、
/// 他の呼び出しは新しいプロセスに対して再試行する。
/// </para>
/// </remarks>
public sealed class PythonServiceSupervisor : IPythonApiClient, IAsyncDisposable
{
    private readonly Func<PythonProcessManager> _processManagerFactory;
    private readonly IPythonApiClient _apiClient;
    private readonly SemaphoreSlim _respawnLock = new(1, 1);

    private PythonProcessManager? _currentManager;
    private AppSettings? _startupSettings;
    private ModelDownloadManager? _modelDownloadManager;
    private bool _disposed;

    /// <summary>連続失敗としてカウントされた回数（成功のたびに0へリセット）。診断・UI表示用。</summary>
    private int _consecutiveFailureCount;

    /// <summary>
    /// 稼働中のPythonプロセスがハンドシェイク完了後に異常終了した際に発火する
    /// （<see cref="PythonProcessManager.ProcessCrashed"/>をそのまま中継）。リスポーンの成否とは独立に、
    /// 「クラッシュが発生した」という一次情報としてUI層のログ・診断表示に使える。
    /// </summary>
    public event EventHandler<PythonProcessCrashedEventArgs>? ProcessCrashed;

    /// <summary>
    /// API呼び出し失敗 → 自動リスポーン＋再試行を行っても復旧できなかった場合（連続失敗）に発火する。
    /// UI層はこれを購読して、ユーザーへの警告表示等に使う（仕様書§7.2-3の「連続失敗時のUI通知」）。
    /// </summary>
    public event EventHandler<PythonServiceDegradedEventArgs>? ServiceDegraded;

    /// <summary>抽出本文をPythonへ渡した分類処理の開始時と完了時に発火する。</summary>
    public event EventHandler<PythonAnalysisStateChangedEventArgs>? AnalysisStateChanged;

    /// <summary>現在の連続失敗回数（成功のたびに0へリセットされる）。</summary>
    public int ConsecutiveFailureCount => _consecutiveFailureCount;

    /// <summary>新しい<see cref="PythonServiceSupervisor"/>を作成する。</summary>
    /// <param name="processManagerFactory">
    /// リスポーンのたびに新しい<see cref="PythonProcessManager"/>インスタンスを生成するファクトリ。
    /// 同一の<see cref="JobObjectManager"/>を毎回渡すクロージャにすること（Job Objectはアプリ全体で1つを再利用する）。
    /// 例: <c>() =&gt; PythonProcessManager.CreateForPyService(jobObjectManager, repoRoot, pythonExe)</c>
    /// </param>
    /// <param name="apiClient">HTTP呼び出しを担当するクライアント。リスポーンのたびに<c>Configure</c>を呼び直す。</param>
    public PythonServiceSupervisor(Func<PythonProcessManager> processManagerFactory, IPythonApiClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(processManagerFactory);
        ArgumentNullException.ThrowIfNull(apiClient);

        _processManagerFactory = processManagerFactory;
        _apiClient = apiClient;
    }

    /// <summary>Pythonプロセスを起動し、ハンドシェイク完了後にAPIクライアントを構成する。</summary>
    public async Task<PythonHandshakeResult> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _startupSettings = null;
        _modelDownloadManager = null;

        PythonProcessManager manager = _processManagerFactory();
        PythonHandshakeResult handshake;
        try
        {
            handshake = await manager.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            manager.Dispose();
            throw;
        }

        AttachManager(manager, handshake);
        return handshake;
    }

    /// <summary>
    /// SLM事前配置モデル統合版の起動（<see cref="PythonProcessManager.StartAsync(AppSettings, ModelDownloadManager, IProgress{double}?, CancellationToken)"/>）。
    /// 初回起動とリスポーンの双方で同じ設定を使用する。リスポーン時の確認はローカルファイルの
    /// 存在・サイズ確認だけで、初回に配置済みなら再ダウンロードは発生しない。これにより
    /// <c>ANALYZER_SLM_MODEL</c>を再起動後の子プロセスにも確実に引き継ぐ。
    /// </summary>
    public async Task<PythonHandshakeResult> StartAsync(
        AppSettings settings,
        ModelDownloadManager modelDownloadManager,
        IProgress<double>? modelDownloadProgress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelDownloadManager);

        // リスポーン後も初回と同じモデル設定を子プロセスへ渡すため保持する。
        // 抽出本文やファイル内容は保持しない。
        _startupSettings = settings;
        _modelDownloadManager = modelDownloadManager;

        PythonProcessManager manager = _processManagerFactory();
        PythonHandshakeResult handshake;
        try
        {
            handshake = await manager.StartAsync(settings, modelDownloadManager, modelDownloadProgress, ct).ConfigureAwait(false);
        }
        catch
        {
            manager.Dispose();
            throw;
        }

        AttachManager(manager, handshake);
        return handshake;
    }

    private void AttachManager(PythonProcessManager manager, PythonHandshakeResult handshake)
    {
        manager.ProcessCrashed += OnManagerProcessCrashed;
        _currentManager = manager;
        _apiClient.Configure(handshake.Port, handshake.Token);
    }

    private void OnManagerProcessCrashed(object? sender, PythonProcessCrashedEventArgs e)
        => ProcessCrashed?.Invoke(this, e);

    /// <summary>
    /// <see cref="IPythonApiClient"/>としてDIできるようにするための互換メソッド。
    /// Supervisor自身が起動ハンドシェイク結果を管理するので、通常のComposition Rootから
    /// このメソッドを直接呼ぶ必要はない。
    /// </summary>
    public void Configure(int port, string bearerToken)
        => _apiClient.Configure(port, bearerToken);

    /// <summary>
    /// <see cref="IPythonApiClient.HealthCheckAsync"/>を、失敗時の自動リスポーン＋1回再試行付きで呼び出す。
    /// </summary>
    public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        ExecuteWithRespawnRetryAsync(
            client => client.HealthCheckAsync(ct),
            success => success,
            operationName: "HealthCheck",
            ct);

    /// <summary>
    /// <see cref="IPythonApiClient.AnalyzeAsync"/>を、失敗時の自動リスポーン＋1回再試行付きで呼び出す。
    /// </summary>
    public async Task<AnalyzeResponse?> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct = default)
    {
        AnalysisStateChanged?.Invoke(this, new PythonAnalysisStateChangedEventArgs { IsRunning = true });

        AnalyzeResponse? result = null;
        Exception? error = null;
        try
        {
            result = await ExecuteWithRespawnRetryAsync(
                client => client.AnalyzeAsync(request, ct),
                response => response is not null,
                operationName: "Analyze",
                ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            AnalysisStateChanged?.Invoke(this, new PythonAnalysisStateChangedEventArgs
            {
                IsRunning = false,
                Response = result,
                Error = error,
            });
        }
    }

    private async Task<TResult> ExecuteWithRespawnRetryAsync<TResult>(
        Func<IPythonApiClient, Task<TResult>> call,
        Func<TResult, bool> isSuccess,
        string operationName,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureStarted();

        TResult result = await call(_apiClient).ConfigureAwait(false);
        if (isSuccess(result))
        {
            Interlocked.Exchange(ref _consecutiveFailureCount, 0);
            return result;
        }

        // 仕様書§7.2-3: 呼び出し失敗を検知したら、1回だけ自動リスポーン＋ハンドシェイク再試行を行う。
        (bool attempted, bool succeeded, Exception? exception) = await TryRespawnOnceAsync(ct).ConfigureAwait(false);

        if (!succeeded)
        {
            RaiseServiceDegraded(operationName, attempted, succeeded: false, exception);
            return result;
        }

        TResult retryResult = await call(_apiClient).ConfigureAwait(false);
        if (isSuccess(retryResult))
        {
            Interlocked.Exchange(ref _consecutiveFailureCount, 0);
            return retryResult;
        }

        // リスポーンには成功したが、再試行したAPI呼び出し自体が再度失敗 = 連続失敗。
        RaiseServiceDegraded(operationName, attempted, succeeded: true, exception: null);
        return retryResult;
    }

    private void RaiseServiceDegraded(string operationName, bool respawnAttempted, bool succeeded, Exception? exception)
    {
        Interlocked.Increment(ref _consecutiveFailureCount);
        ServiceDegraded?.Invoke(this, new PythonServiceDegradedEventArgs
        {
            OperationName = operationName,
            RespawnAttempted = respawnAttempted,
            RespawnSucceeded = succeeded,
            RespawnException = exception,
        });
    }

    /// <summary>
    /// 1回だけリスポーンを行う。同時に複数の呼び出しが失敗した場合でも、実際にプロセスを起動し直すのは
    /// 1回のみ（<see cref="_respawnLock"/>で直列化し、他呼び出しが既にリスポーン済みなら成功扱いで即戻る）。
    /// </summary>
    private async Task<(bool Attempted, bool Succeeded, Exception? Exception)> TryRespawnOnceAsync(CancellationToken ct)
    {
        PythonProcessManager? managerBeforeRespawn = _currentManager;

        await _respawnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_currentManager, managerBeforeRespawn))
            {
                // 他の呼び出しが既にリスポーン済み（多発失敗の同時発生）。二重リスポーンを避けて成功扱いにする。
                return (Attempted: false, Succeeded: true, Exception: null);
            }

            PythonProcessManager newManager = _processManagerFactory();
            PythonHandshakeResult handshake;
            try
            {
                handshake = _startupSettings is not null && _modelDownloadManager is not null
                    ? await newManager.StartAsync(_startupSettings, _modelDownloadManager, null, ct).ConfigureAwait(false)
                    : await newManager.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                newManager.Dispose();
                return (Attempted: true, Succeeded: false, Exception: ex);
            }

            PythonProcessManager? oldManager = Interlocked.Exchange(ref _currentManager, newManager);
            if (oldManager is not null)
            {
                oldManager.ProcessCrashed -= OnManagerProcessCrashed;
                oldManager.Dispose(); // 既にクラッシュ済みのはずだが、念のため後始末する。
            }

            newManager.ProcessCrashed += OnManagerProcessCrashed;
            _apiClient.Configure(handshake.Port, handshake.Token);

            return (Attempted: true, Succeeded: true, Exception: null);
        }
        finally
        {
            _respawnLock.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_currentManager is null)
        {
            throw new InvalidOperationException(
                $"{nameof(PythonServiceSupervisor)}.{nameof(StartAsync)}が呼ばれる前にAPIを呼び出しました。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_currentManager is { } manager)
        {
            manager.ProcessCrashed -= OnManagerProcessCrashed;
            manager.Dispose();
            _currentManager = null;
        }

        _respawnLock.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
