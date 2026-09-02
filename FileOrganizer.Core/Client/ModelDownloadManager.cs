using System;
using System.IO;
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
/// 仕様書§3.1「デモ用事前配置モデルフラグ」（Gemma-2-2B-Q4_K_M GGUF、約1.5GB）の土台。
/// <see cref="AppSettings.UsePreloadedSlmModel"/>/<see cref="AppSettings.SlmModelPath"/>を見て、
/// 事前配置モデルの存在確認（ファイル存在・簡易サイズチェック）を行う（<see cref="CheckPreloadedModelAsync"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本クラスの位置づけ（雛形）</b>: 事前配置モデルの確認ロジックは実装済み。未配置時のオンデマンド
/// ダウンロード（<see cref="DownloadModelAsync"/>）は、<see cref="IProgress{T}"/>&lt;double&gt;による
/// 進捗通知付きのインターフェース（シグネチャ）のみをここで確定し、FileOrganizer.UI側の
/// 「SLMモデル取得進捗バー」（仕様書§4.1）が本メソッドを先行して呼び出せるようにする。
/// 実ダウンロード先URL（Gemma-2-2Bモデル配布元）はPhase3で確定するため、実処理は未実装
/// （<see cref="NotImplementedException"/>）。
/// </para>
/// <para>
/// <b>簡易サイズチェック</b>: 100MB以上の大容量ファイルに対する<c>HashHelper</c>と同様の考え方で、
/// 起動時の即時判定を優先しフルハッシュ検証は行わない。期待サイズ
/// （<see cref="ExpectedModelSizeBytes"/>）の<see cref="MinimumSizeRatio"/>未満しか無い場合のみ、
/// ダウンロード中断・破損の疑いとして「未配置」（<see cref="ModelAvailabilityStatus.SizeTooSmall"/>）扱いにする。
/// </para>
/// </remarks>
public sealed class ModelDownloadManager
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
    /// 事前配置モデルの存在確認（ファイル存在・簡易サイズチェック）を行う。
    /// </summary>
    /// <param name="settings">
    /// <see cref="AppSettings.UsePreloadedSlmModel"/>/<see cref="AppSettings.SlmModelPath"/>を含む設定。
    /// </param>
    /// <param name="ct">キャンセルトークン</param>
    public Task<ModelAvailabilityResult> CheckPreloadedModelAsync(AppSettings settings, CancellationToken ct = default)
    {
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
    /// 未配置時のオンデマンドダウンロード（雛形・未実装）。
    /// </summary>
    /// <param name="destinationPath">
    /// ダウンロード先の保存先パス（通常は確定後の<see cref="AppSettings.SlmModelPath"/>に設定する値）。
    /// </param>
    /// <param name="progress">
    /// 0.0（開始）〜1.0（完了）の進捗率を報告するプログレス通知先（省略可）。
    /// FileOrganizer.UIの「SLMモデル取得進捗バー」（仕様書§4.1）が購読する想定。
    /// </param>
    /// <param name="ct">キャンセルトークン</param>
    /// <exception cref="NotImplementedException">
    /// 実ダウンロード先URL（Gemma-2-2B-Q4_K_M GGUF配布元）がPhase3で確定するまでは常にスローする。
    /// Phase3実装時は、HttpClientによるストリーミングダウンロード＋
    /// <c>progress.Report(受信バイト数 / Content-Length)</c>に置き換える想定
    /// （このシグネチャ自体はUI側との結合点として変更しない）。
    /// </exception>
    public Task<ModelDownloadResult> DownloadModelAsync(
        string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // 【Phase3で実装】配布元URL確定後、ストリーミングダウンロード + 進捗報告 + 簡易サイズチェック
        // （CheckPreloadedModelAsyncと同じ基準）を実装する。
        throw new NotImplementedException(
            "モデルのオンデマンドダウンロードはPhase3で実装予定です（配布元URL未確定のため本雛形では未実装）。");
    }
}
