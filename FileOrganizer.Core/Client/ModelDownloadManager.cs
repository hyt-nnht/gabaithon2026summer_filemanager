using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Client;

/// <summary><see cref="ModelDownloadManager.CheckPreloadedModelAsync"/>の判定結果種別。</summary>
public enum ModelAvailabilityStatus
{
    /// <summary>事前配置モデルが存在し、サイズも妥当 — 即座に推論へ使用できる（DL待ち時間ゼロ）。</summary>
    Ready,

    /// <summary><see cref="AppSettings.UsePreloadedSlmModel"/>が<c>false</c>（事前配置モデルを使わない設定）。</summary>
    Disabled,

    /// <summary><see cref="AppSettings.UsePreloadedSlmModel"/>は<c>true</c>だが<see cref="AppSettings.SlmModelPath"/>が未設定。</summary>
    PathNotConfigured,

    /// <summary>指定パスにファイルが存在しない（未配置、またはオンデマンドDLが必要）。</summary>
    FileNotFound,

    /// <summary>ファイルは存在するが、期待サイズ（<see cref="ModelDownloadManager.ExpectedModelSizeBytes"/>）を大きく下回る
    /// （ダウンロード中断・破損の疑い。オンデマンドDLでの再取得が必要）。</summary>
    SizeTooSmall,
}

/// <summary><see cref="ModelDownloadManager.CheckPreloadedModelAsync"/>の結果。</summary>
public sealed class ModelAvailabilityResult
{
    public required ModelAvailabilityStatus Status { get; init; }

    /// <summary>判定対象としたパス（<see cref="ModelAvailabilityStatus.Disabled"/>/<see cref="ModelAvailabilityStatus.PathNotConfigured"/>時は<c>null</c>）。</summary>
    public string? ModelPath { get; init; }

    /// <summary>実際のファイルサイズ（ファイルが存在した場合のみ）。</summary>
    public long? ActualSizeBytes { get; init; }

    /// <summary>即座に推論へ使用できる状態か（<see cref="Status"/>が<see cref="ModelAvailabilityStatus.Ready"/>）。</summary>
    public bool IsReady => Status == ModelAvailabilityStatus.Ready;
}

/// <summary><see cref="ModelDownloadManager.DownloadModelAsync"/>の結果。</summary>
public sealed class ModelDownloadResult
{
    public required bool Success { get; init; }
    public string? ModelPath { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 仕様書§3.1「デモ用事前配置モデルフラグ」（Gemma-2-2B-Q4_K_M GGUF、約1.5GB）を担当する。
/// <see cref="AppSettings.UsePreloadedSlmModel"/>/<see cref="AppSettings.SlmModelPath"/>を見て、
/// 事前配置モデルの存在確認（ファイル存在・簡易サイズチェック）を行い（<see cref="CheckPreloadedModelAsync"/>）、
/// 未配置・破損時は<see cref="IProgress{T}"/>&lt;double&gt;による進捗通知付きでオンデマンドダウンロードを行う
/// （<see cref="DownloadModelAsync"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>簡易サイズチェック</b>: 100MB以上の大容量ファイルに対する<c>HashHelper</c>と同様の考え方で、
/// 起動時の即時判定を優先しフルハッシュ検証は行わない。期待サイズ
/// （<see cref="ExpectedModelSizeBytes"/>）の<see cref="MinimumSizeRatio"/>未満しか無い場合のみ、
/// ダウンロード中断・破損の疑いとして「未配置」（<see cref="ModelAvailabilityStatus.SizeTooSmall"/>）扱いにする。
/// オンデマンドダウンロード完了直後も同じ基準でファイルサイズを検証する。
/// </para>
/// <para>
/// <b>DL待ち時間ゼロ化（<see cref="PythonProcessManager"/>との統合）</b>: 本クラス単体は「確認」と「取得」の
/// 責務のみを持ち、Pythonプロセス起動前にこれらを呼び出す統合フローは
/// <see cref="PythonProcessManager.StartAsync(AppSettings, ModelDownloadManager, IProgress{double}?, System.Threading.CancellationToken)"/>
/// 側に置く（事前配置モデルが認識できればダウンロードをスキップして即起動、なければ本クラスの
/// <see cref="DownloadModelAsync"/>の進捗をUIへ中継する）。
/// </para>
/// <para>
/// <b>配布元URL</b>: <see cref="DefaultModelSourceUri"/>はPhase3時点の暫定値（Gemma-2-2B-Q4_K_M GGUF配布先）。
/// チーム合意後に正式なホスティング先へ更新するか、コンストラクタの<c>sourceUri</c>引数で上書きすること
/// （審査デモでは<see cref="AppSettings.UsePreloadedSlmModel"/>を有効化した事前配置運用を基本とし、
/// 本ダウンロード経路は事前配置が使えない環境向けのフォールバックと位置づける）。
/// </para>
/// </remarks>
public sealed class ModelDownloadManager : IDisposable
{
    /// <summary>Gemma-2-2B-Q4_K_M GGUFの想定サイズ（仕様書§3.1「約1.5GB」）。簡易サイズチェックの基準値。</summary>
    public const long ExpectedModelSizeBytes = 1_500_000_000L; // 約1.5GB

    /// <summary>
    /// 簡易サイズチェックの下限比率。<see cref="ExpectedModelSizeBytes"/>のこの割合未満しか無い場合、
    /// ダウンロード中断や破損の疑いがあるとみなし「未配置」として扱う
    /// （量子化方式・モデルバージョン差による多少のサイズ差は許容する緩い閾値）。
    /// </summary>
    public const double MinimumSizeRatio = 0.5;

    /// <summary>
    /// <see cref="DownloadModelAsync"/>の既定ダウンロード元（Gemma-2-2B-Q4_K_M GGUF）。
    /// 【要確定】Phase3時点の暫定URL。正式な配布元が決まり次第、コンストラクタの<c>sourceUri</c>引数、
    /// または本定数を更新すること。
    /// </summary>
    public static readonly Uri DefaultModelSourceUri =
        new("https://huggingface.co/bartowski/gemma-2-2b-it-GGUF/resolve/main/gemma-2-2b-it-Q4_K_M.gguf");

    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(30);

    private const int DownloadBufferSize = 81_920; // 80KB

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _sourceUri;
    private bool _disposed;

    /// <summary>
    /// 新規に内部管理のHttpClientを生成するコンストラクタ。
    /// </summary>
    /// <param name="sourceUri">ダウンロード元（省略時は<see cref="DefaultModelSourceUri"/>）。</param>
    /// <param name="downloadTimeout">
    /// ダウンロード全体のタイムアウト。既定30分（約1.5GBを低速回線でも取得しきれる余裕を持たせている）。
    /// </param>
    public ModelDownloadManager(Uri? sourceUri = null, TimeSpan? downloadTimeout = null)
        : this(new HttpClient(), ownsHttpClient: true, sourceUri, downloadTimeout)
    {
    }

    /// <summary>
    /// テスト等でHttpClient（差し替え可能なHttpMessageHandlerを持つもの）を注入するコンストラクタ。
    /// 渡した<paramref name="httpClient"/>の所有権は呼び出し元に残り、Disposeでは破棄しない。
    /// </summary>
    public ModelDownloadManager(HttpClient httpClient, Uri? sourceUri = null, TimeSpan? downloadTimeout = null)
        : this(httpClient, ownsHttpClient: false, sourceUri, downloadTimeout)
    {
    }

    private ModelDownloadManager(HttpClient httpClient, bool ownsHttpClient, Uri? sourceUri, TimeSpan? downloadTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _sourceUri = sourceUri ?? DefaultModelSourceUri;
        _httpClient.Timeout = downloadTimeout ?? DefaultDownloadTimeout;
    }

    /// <summary>
    /// 事前配置モデルの存在確認（ファイル存在・簡易サイズチェック）を行う。
    /// </summary>
    /// <param name="settings">
    /// <see cref="AppSettings.UsePreloadedSlmModel"/>/<see cref="AppSettings.SlmModelPath"/>を含む設定。
    /// </param>
    /// <param name="ct">キャンセルトークン</param>
    public Task<ModelAvailabilityResult> CheckPreloadedModelAsync(AppSettings settings, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        if (!settings.UsePreloadedSlmModel)
        {
            return Task.FromResult(new ModelAvailabilityResult { Status = ModelAvailabilityStatus.Disabled });
        }

        if (string.IsNullOrWhiteSpace(settings.SlmModelPath))
        {
            return Task.FromResult(new ModelAvailabilityResult { Status = ModelAvailabilityStatus.PathNotConfigured });
        }

        var fileInfo = new FileInfo(settings.SlmModelPath);
        if (!fileInfo.Exists)
        {
            return Task.FromResult(new ModelAvailabilityResult
            {
                Status = ModelAvailabilityStatus.FileNotFound,
                ModelPath = settings.SlmModelPath,
            });
        }

        long minimumBytes = (long)(ExpectedModelSizeBytes * MinimumSizeRatio);
        if (fileInfo.Length < minimumBytes)
        {
            return Task.FromResult(new ModelAvailabilityResult
            {
                Status = ModelAvailabilityStatus.SizeTooSmall,
                ModelPath = settings.SlmModelPath,
                ActualSizeBytes = fileInfo.Length,
            });
        }

        return Task.FromResult(new ModelAvailabilityResult
        {
            Status = ModelAvailabilityStatus.Ready,
            ModelPath = settings.SlmModelPath,
            ActualSizeBytes = fileInfo.Length,
        });
    }

    /// <summary>
    /// 未配置時のオンデマンドダウンロード。<see cref="_sourceUri"/>からストリーミング取得し、
    /// 受信バイト数に応じて<paramref name="progress"/>へ0.0〜1.0の進捗率を報告する。
    /// </summary>
    /// <param name="destinationPath">
    /// ダウンロード先の保存先パス（通常は確定後の<see cref="AppSettings.SlmModelPath"/>に設定する値）。
    /// 保存先ディレクトリが存在しない場合は自動作成する。
    /// </param>
    /// <param name="progress">
    /// 0.0（開始）〜1.0（完了）の進捗率を報告するプログレス通知先（省略可）。
    /// FileOrganizer.UIの「SLMモデル取得進捗バー」（仕様書§4.1）が購読する想定。
    /// </param>
    /// <param name="ct">キャンセルトークン</param>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/>が空の場合。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> がキャンセルされた場合。</exception>
    /// <remarks>
    /// HTTP接続失敗・非2xx応答・ダウンロード後のサイズ不足（破損の疑い）は例外を投げず、
    /// <see cref="ModelDownloadResult.Success"/><c>=false</c> + <see cref="ModelDownloadResult.ErrorMessage"/>
    /// として呼び出し元へ返す（<see cref="PythonProcessManager"/>統合フローでの一律なエラーハンドリングを想定）。
    /// ダウンロード中のファイルは一時ファイル（<c>*.download</c>）へ書き出し、完了後に
    /// <paramref name="destinationPath"/>へリネームする。これにより、ダウンロード途中のファイルを
    /// <see cref="CheckPreloadedModelAsync"/>が「配置済みモデル」と誤認することを防ぐ。
    /// </remarks>
    public async Task<ModelDownloadResult> DownloadModelAsync(
        string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ct.ThrowIfCancellationRequested();

        string? destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        // ダウンロード中は一時ファイル名で書き出し、完了後にリネームする（未完成ファイルの誤認防止）。
        string tempPath = destinationPath + ".download";

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(_sourceUri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ModelDownloadResult
                {
                    Success = false,
                    ErrorMessage = $"モデルのダウンロードに失敗しました（HTTP {(int)response.StatusCode} {response.ReasonPhrase}）。",
                };
            }

            long? contentLength = response.Content.Headers.ContentLength;
            long expectedTotal = contentLength is > 0 ? contentLength.Value : ExpectedModelSizeBytes;

            progress?.Report(0.0);

            await using (Stream httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true))
            {
                var buffer = new byte[DownloadBufferSize];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalRead += bytesRead;

                    double ratio = expectedTotal > 0 ? Math.Clamp((double)totalRead / expectedTotal, 0.0, 1.0) : 0.0;
                    progress?.Report(ratio);
                }
            }

            long minimumBytes = (long)(ExpectedModelSizeBytes * MinimumSizeRatio);
            var downloadedInfo = new FileInfo(tempPath);
            if (downloadedInfo.Length < minimumBytes)
            {
                DeleteQuietly(tempPath);
                return new ModelDownloadResult
                {
                    Success = false,
                    ErrorMessage = $"ダウンロードされたファイルサイズが想定より小さすぎます" +
                        $"（{downloadedInfo.Length:N0} bytes < 最低{minimumBytes:N0} bytes）。ダウンロードが中断された可能性があります。",
                };
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            progress?.Report(1.0);

            return new ModelDownloadResult { Success = true, ModelPath = destinationPath };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DeleteQuietly(tempPath);
            throw;
        }
        catch (HttpRequestException ex)
        {
            DeleteQuietly(tempPath);
            return new ModelDownloadResult
            {
                Success = false,
                ErrorMessage = $"モデルのダウンロード中に通信エラーが発生しました: {ex.Message}",
            };
        }
        catch (IOException ex)
        {
            DeleteQuietly(tempPath);
            return new ModelDownloadResult
            {
                Success = false,
                ErrorMessage = $"モデルのダウンロード中にファイルI/Oエラーが発生しました: {ex.Message}",
            };
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 後始末失敗は無視。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
