using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FileOrganizer.Core.Win32;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Client;

/// <summary>
/// Embedded Python（py_service）子プロセスの起動と、AI_IMPLEMENTATION_GUIDE.md §3.1
/// 起動ハンドシェイク（stdout読取によるポート確定）を担当する。
/// 仕様書§7.2-5「Port 0動的割当およびBearer Token認証」の起動側実装。
/// </summary>
/// <remarks>
/// ハンドシェイク手順:
/// 1. 暗号論的乱数で32文字のBearerトークンを生成し、環境変数 <c>ORGANIZER_IPC_TOKEN</c> にセットして
///    Pythonプロセスを起動する。
/// 2. Python（uvicorn/FastAPI）は Port 0 でバインドし、起動完了直後に標準出力へ
///    <c>PORT: {number}</c> を1行出力する想定。この行を検知したらポート番号を確定する。
/// 3. 起動したプロセスは道連れ終了のため <see cref="JobObjectManager"/> に割り当てる。
/// 4. ハンドシェイクが既定10秒以内に完了しない場合は <see cref="TimeoutException"/> をスローする。
///
/// 【解消済み】本クラスは起動対象の実行ファイル/引数を外部から指定できる設計にしている。
/// 以前は「リポジトリ同梱の<c>py_service</c>が<c>py_service/main.py</c>を持たない」等、
/// 本仕様（§3.1 / CONTRACTS.md）とのずれを理由に<see cref="CreateForPyService"/>の使用を
/// 見合わせる注意書きがあったが、現在の<c>py_service/main.py</c>は<c>PORT: {number}</c>
/// （コロンあり）をstdoutへ出力し、環境変数名も<c>ORGANIZER_IPC_TOKEN</c>で一致している
/// ことをNFR_INTEGRATION_TEST_REPORT.md §6の実py_service E2Eで確認済み。
/// <see cref="CreateForPyService"/>はそのまま使用してよい（<c>--port 0</c>を自動付与する）。
/// </remarks>
public sealed class PythonProcessManager : IDisposable
{
    /// <summary>Bearerトークンの文字数（16進数文字）。AI_IMPLEMENTATION_GUIDE.md §3.1 準拠。</summary>
    public const int TokenLength = 32;

    /// <summary>
    /// SLMモデルの配置先パスをPython子プロセスへ伝える環境変数名。
    /// <c>py_service/src/file_analyzer/config.py</c>の<c>Settings.from_env</c>が読む変数名と一致させること。
    /// </summary>
    private const string SlmModelPathEnvironmentVariableName = "ANALYZER_SLM_MODEL";

    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex PortLinePattern = new(@"^PORT:\s*(\d+)\s*$", RegexOptions.Compiled);

    private readonly JobObjectManager _jobObjectManager;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string _workingDirectory;
    private readonly TimeSpan _handshakeTimeout;

    private readonly object _stateLock = new();
    private Process? _process;
    private bool _started;
    private bool _disposed;
    private volatile bool _intentionalStop;
    private volatile bool _crashReported;

    /// <summary>
    /// ハンドシェイク完了後（正常稼働中）に、C#側の意図（<see cref="Dispose"/>呼び出し等）によらず
    /// プロセスが終了した場合に発火する。仕様書§7.2-3「推論中のOOM等でPythonプロセスが異常終了した場合」の
    /// 検知シグナル。<c>Process.Exited</c>イベントと、<see cref="JobObjectManager.IsProcessActive"/>による
    /// Job Object側の実プロセス一覧突き合わせを組み合わせて誤検知を防いでいる（詳細は<see cref="OnProcessExitedAfterHandshake"/>）。
    /// ハンドラは<see cref="Process.Exited"/>由来のスレッドプールスレッドから呼び出される点に注意。
    /// </summary>
    public event EventHandler<PythonProcessCrashedEventArgs>? ProcessCrashed;

    public PythonProcessManager(
        JobObjectManager jobObjectManager,
        string fileName,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        TimeSpan? handshakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(jobObjectManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _jobObjectManager = jobObjectManager;
        _fileName = fileName;
        _arguments = arguments ?? [];
        _workingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        _handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;
    }

    /// <summary>
    /// 本仕様（py_service/main.py・PORT: {number}・ORGANIZER_IPC_TOKEN）どおりの構成で
    /// py_serviceを起動するためのファクトリ。前提が整うまでは<see cref="PythonProcessManager"/>の
    /// 型コメントを参照し、必要に応じて呼び出し側でfileName/argumentsを直接指定すること。
    /// </summary>
    public static PythonProcessManager CreateForPyService(
        JobObjectManager jobObjectManager,
        string repositoryRootDirectory,
        string pythonExecutable = "python",
        TimeSpan? handshakeTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootDirectory);

        string workingDirectory = Path.Combine(repositoryRootDirectory, "py_service");
        string scriptPath = Path.Combine(workingDirectory, "main.py");
        // --port 0 を明示しないと main.py は既定の固定ポート(8765)でuvicornを起動し、
        // "PORT: {number}" 行を一切出力しないためハンドシェイクが必ずタイムアウトする
        // （py_service/src/file_analyzer/__main__.py 参照）。
        return new PythonProcessManager(jobObjectManager, pythonExecutable, [scriptPath, "--port", "0"], workingDirectory, handshakeTimeout);
    }

    /// <summary>起動済みプロセス（未起動なら<c>null</c>）。停止/監視用に読み取り専用で公開。</summary>
    public Process? Process => _process;

    /// <summary>
    /// Pythonプロセスを起動し、起動ハンドシェイクが完了するまで待機する。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 既に起動済み、プロセス起動自体に失敗、またはハンドシェイク完了前にプロセスが終了した場合。
    /// </exception>
    /// <exception cref="TimeoutException">既定（または指定）のタイムアウト内にポートが確定しなかった場合。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> がキャンセルされた場合。</exception>
    public async Task<PythonHandshakeResult> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MarkStarted();
        return await StartProcessCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SLM事前配置モデルの確認/オンデマンドダウンロードを、Pythonプロセスの起動前に統合した起動メソッド。
    /// モデルが利用可能（事前配置認識 or オンデマンドダウンロード成功）になった場合、そのモデルパスを
    /// 環境変数 <c>ANALYZER_SLM_MODEL</c> としてPython子プロセスへ渡す
    /// （<c>py_service/src/file_analyzer/config.py</c>の<c>Settings.from_env</c>が読む変数名と一致させる）。
    /// これにより、C#側でモデル準備が完了したにもかかわらずpy_serviceがモデルパスを認識できず
    /// SLM推論が機能しない、という不整合を防ぐ。
    /// </summary>
    /// <param name="settings">
    /// <see cref="AppSettings.UsePreloadedSlmModel"/>/<see cref="AppSettings.SlmModelPath"/>を含む設定。
    /// <see cref="ModelDownloadManager.CheckPreloadedModelAsync"/>にそのまま渡される。
    /// </param>
    /// <param name="modelDownloadManager">事前配置モデルの確認・オンデマンドダウンロードを担当するマネージャー。</param>
    /// <param name="modelDownloadProgress">
    /// オンデマンドダウンロードが発生した場合にのみ、0.0〜1.0の進捗率を受け取るプログレス通知先（省略可）。
    /// 事前配置モデルが認識できた場合はダウンロードが発生しないため、通知は行われない
    /// （即座に1.0を報告し、UI側の進捗バーを即完了扱いにできるようにする）。
    /// </param>
    /// <param name="ct">キャンセルトークン</param>
    /// <exception cref="InvalidOperationException">
    /// 既に起動済み、オンデマンドダウンロードに失敗、プロセス起動自体に失敗、
    /// またはハンドシェイク完了前にプロセスが終了した場合。
    /// </exception>
    /// <exception cref="TimeoutException">既定（または指定）のタイムアウト内にポートが確定しなかった場合。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> がキャンセルされた場合。</exception>
    public async Task<PythonHandshakeResult> StartAsync(
        AppSettings settings,
        ModelDownloadManager modelDownloadManager,
        IProgress<double>? modelDownloadProgress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelDownloadManager);

        MarkStarted();
        string? readyModelPath = await EnsureSlmModelReadyAsync(settings, modelDownloadManager, modelDownloadProgress, ct)
            .ConfigureAwait(false);

        IReadOnlyDictionary<string, string>? extraEnvironmentVariables = null;
        if (!string.IsNullOrWhiteSpace(readyModelPath))
        {
            extraEnvironmentVariables = new Dictionary<string, string>
            {
                [SlmModelPathEnvironmentVariableName] = readyModelPath,
            };
        }

        return await StartProcessCoreAsync(ct, extraEnvironmentVariables).ConfigureAwait(false);
    }

    private void MarkStarted()
    {
        lock (_stateLock)
        {
            if (_started)
            {
                throw new InvalidOperationException("このPythonProcessManagerは既に起動済みです。");
            }
            _started = true;
        }
    }

    /// <summary>
    /// Pythonプロセス起動前に、事前配置モデルの有無を確認する。
    /// 認識できれば（<see cref="ModelAvailabilityResult.IsReady"/>）ダウンロードを一切行わず即座に戻る
    /// （仕様書§3.1「DL待ち時間ゼロ化」）。SLM事前配置機能自体が無効・未設定の場合もダウンロード対象外として
    /// 即座に戻る（SLM機能を使わない構成として扱う）。未配置・破損（サイズ不足）の場合のみ、
    /// <see cref="ModelDownloadManager.DownloadModelAsync"/>によるオンデマンドダウンロードを行い、
    /// その進捗を<paramref name="progress"/>へ中継する。
    /// </summary>
    /// <returns>
    /// モデルが利用可能になった（事前配置認識 or ダウンロード成功）場合は<paramref name="settings"/>の
    /// <see cref="AppSettings.SlmModelPath"/>。SLM機能を使わない構成（無効・保存先未設定）の場合は<see langword="null"/>。
    /// 呼び出し元はこの戻り値をPython子プロセスの<c>ANALYZER_SLM_MODEL</c>環境変数へ渡す。
    /// </returns>
    private static async Task<string?> EnsureSlmModelReadyAsync(
        AppSettings settings,
        ModelDownloadManager modelDownloadManager,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        ModelAvailabilityResult availability = await modelDownloadManager
            .CheckPreloadedModelAsync(settings, ct)
            .ConfigureAwait(false);

        if (availability.IsReady)
        {
            // 事前配置モデルを認識済み → ダウンロード不要で即起動。UI側の進捗バーも即完了扱いにできるよう通知する。
            progress?.Report(1.0);
            return settings.SlmModelPath;
        }

        if (availability.Status is ModelAvailabilityStatus.Disabled or ModelAvailabilityStatus.PathNotConfigured)
        {
            // SLM事前配置機能が無効、または保存先未設定 → オンデマンドダウンロード対象外（SLM機能を使わない構成）。
            return null;
        }

        // FileNotFound / SizeTooSmall → オンデマンドダウンロードで取得し、進捗をUIへ中継する。
        ModelDownloadResult downloadResult = await modelDownloadManager
            .DownloadModelAsync(settings.SlmModelPath, progress, ct)
            .ConfigureAwait(false);

        if (!downloadResult.Success)
        {
            throw new InvalidOperationException(
                $"SLMモデルのオンデマンドダウンロードに失敗しました: {downloadResult.ErrorMessage}");
        }

        return settings.SlmModelPath;
    }

    /// <param name="ct">キャンセルトークン。</param>
    /// <param name="extraEnvironmentVariables">
    /// <c>ORGANIZER_IPC_TOKEN</c>に加えてPython子プロセスへ渡す追加の環境変数（省略可）。
    /// 現状<see cref="StartAsync(AppSettings, ModelDownloadManager, IProgress{double}?, CancellationToken)"/>が
    /// <c>ANALYZER_SLM_MODEL</c>を渡すために使用する。
    /// </param>
    private async Task<PythonHandshakeResult> StartProcessCoreAsync(
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnvironmentVariables = null)
    {
        string token = RandomNumberGenerator.GetHexString(TokenLength);

        var startInfo = new ProcessStartInfo
        {
            FileName = _fileName,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["ORGANIZER_IPC_TOKEN"] = token;
        if (extraEnvironmentVariables is not null)
        {
            foreach ((string key, string value) in extraEnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        var portCompletionSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrBuffer = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            Match match = PortLinePattern.Match(e.Data.Trim());
            if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
            {
                portCompletionSource.TrySetResult(port);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrBuffer)
                {
                    stderrBuffer.AppendLine(e.Data);
                }
            }
        };

        process.Exited += (_, _) =>
        {
            string stderrText;
            lock (stderrBuffer)
            {
                stderrText = stderrBuffer.ToString();
            }

            bool wasDuringHandshake = portCompletionSource.TrySetException(new InvalidOperationException(
                $"Pythonプロセスが起動ハンドシェイク完了前に終了しました（ExitCode={process.ExitCode}）。" +
                (string.IsNullOrWhiteSpace(stderrText) ? string.Empty : $" stderr: {stderrText.Trim()}")));

            if (!wasDuringHandshake)
            {
                // ハンドシェイク完了後（正常稼働中）の終了 = 異常終了の疑い。クラッシュ検知へ回す。
                OnProcessExitedAfterHandshake(process, stderrText);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Pythonプロセスの起動に失敗しました（FileName={_fileName}）。");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Pythonプロセスの起動に失敗しました（FileName={_fileName}）。", ex);
        }

        _process = process;

        try
        {
            _jobObjectManager.AssignProcess(process);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_handshakeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        await using var registration = linkedCts.Token.Register(
            static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
            portCompletionSource);

        int port;
        try
        {
            port = await portCompletionSource.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new TimeoutException(
                $"Pythonプロセスの起動ハンドシェイクが{_handshakeTimeout.TotalSeconds:F0}秒以内に完了しませんでした（\"PORT: <number>\"行を検知できず）。");
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new OperationCanceledException(ct);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }

        return new PythonHandshakeResult(port, token);
    }

    /// <summary>
    /// ハンドシェイク完了後にプロセスが終了した際、それが異常終了（クラッシュ）かどうかを判定して
    /// <see cref="ProcessCrashed"/>を発火する。仕様書§7.2-3の検知条件どおり、以下2点を組み合わせて判定する。
    /// <list type="number">
    /// <item><description><c>Process.Exited</c>イベント自体（本メソッドの呼び出しトリガー）。</description></item>
    /// <item><description><see cref="JobObjectManager.IsProcessActive"/>によるOS側Job Objectの実プロセス一覧突き合わせ
    /// （Exitedがスプリアスに発火した場合や、PIDが再利用された場合の誤検知を避ける）。</description></item>
    /// </list>
    /// <see cref="Dispose"/>等、C#側が意図して停止させた場合（<see cref="_intentionalStop"/>）は対象外とする。
    /// </summary>
    private void OnProcessExitedAfterHandshake(Process process, string stderrText)
    {
        if (_intentionalStop || _crashReported)
        {
            // 意図した停止（Dispose等）、または既に一度通知済み（二重発火防止）。
            return;
        }

        bool stillActiveInJobObject;
        try
        {
            stillActiveInJobObject = _jobObjectManager.IsProcessActive(process.Id);
        }
        catch
        {
            // Job Object側の照会自体に失敗しても、Process.Exitedの発火という一次情報は無視しない。
            stillActiveInJobObject = false;
        }

        if (stillActiveInJobObject)
        {
            // Job Object側ではまだ稼働中に見える = Process.Exitedのスプリアス発火の可能性が高いため無視する。
            return;
        }

        _crashReported = true;

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        ProcessCrashed?.Invoke(this, new PythonProcessCrashedEventArgs
        {
            ProcessId = process.Id,
            ExitCode = exitCode,
            StderrTail = string.IsNullOrWhiteSpace(stderrText) ? null : stderrText.Trim(),
        });
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 既に終了している場合は無視。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _intentionalStop = true; // これから行うKillQuietlyによるExitedはクラッシュ通知の対象外とする。

        if (_process is { } process)
        {
            KillQuietly(process);
            process.Dispose();
            _process = null;
        }
    }
}
