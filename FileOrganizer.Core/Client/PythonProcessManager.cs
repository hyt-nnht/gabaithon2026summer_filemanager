using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FileOrganizer.Core.Win32;

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
/// 注意: 本クラスは起動対象の実行ファイル/引数を外部から指定できる設計にしている。
/// リポジトリ同梱の <c>py_service</c> は本ドキュメント作成時点で <c>py_service/main.py</c> を
/// 持たず（実際のエントリポイントは <c>python -m file_analyzer</c>）、stdout出力も
/// <c>PORT={port}</c>（コロンなし）、認証トークンの環境変数名も <c>ANALYZER_BEARER_TOKEN</c> と、
/// いずれも本仕様（§3.1 / CONTRACTS.md）と一致していない。Python担当者との合意が取れるまでは
/// <see cref="CreateForPyService"/> をそのまま使わず、コンストラクタへ直接実行ファイル/引数を渡すか、
/// テスト用モックスクリプト（<c>FileOrganizer.Core.Tests/TestAssets/mock_py_service.ps1</c>）で
/// 起動確認を行うこと。
/// </remarks>
public sealed class PythonProcessManager : IDisposable
{
    /// <summary>Bearerトークンの文字数（16進数文字）。AI_IMPLEMENTATION_GUIDE.md §3.1 準拠。</summary>
    public const int TokenLength = 32;

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
        return new PythonProcessManager(jobObjectManager, pythonExecutable, [scriptPath], workingDirectory, handshakeTimeout);
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
        lock (_stateLock)
        {
            if (_started)
            {
                throw new InvalidOperationException("このPythonProcessManagerは既に起動済みです。");
            }
            _started = true;
        }

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

            portCompletionSource.TrySetException(new InvalidOperationException(
                $"Pythonプロセスが起動ハンドシェイク完了前に終了しました（ExitCode={process.ExitCode}）。" +
                (string.IsNullOrWhiteSpace(stderrText) ? string.Empty : $" stderr: {stderrText.Trim()}")));
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

        if (_process is { } process)
        {
            KillQuietly(process);
            process.Dispose();
            _process = null;
        }
    }
}
