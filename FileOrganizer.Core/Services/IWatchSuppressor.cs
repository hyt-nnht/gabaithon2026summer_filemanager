using System;

namespace FileOrganizer.Core.Services;

/// <summary>
/// 【Watcher連携用インターフェースの提案】自アプリのファイル操作結果パスをWatcher側へ伝え、
/// 監視ループを防止するための連携インターフェース。
/// 仕様書§6「監視ループ防止」: 自アプリの移動先が監視対象内である場合、移動イベント発行時に
/// 除外フラグ（冪等性トークン）を照合して再処理を遮断する、を実現するために
/// <see cref="FileOperationService"/>が使用する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileOrganizer.Shared.Contracts.IWatcherService.SuppressPath(string, TimeSpan)"/>と
/// 役割は重なるが、こちらは発行元の操作を一意に識別する冪等性トークンそのものをWatcher側へ
/// 渡せるようシグネチャを拡張している。Watcher側はこのトークンを、抑止中に万一飛んできてしまった
/// イベントの突合や、診断ログでの追跡に利用できる（「Watcher側で参照できる形にする」の要件）。
/// </para>
/// <para>
/// Watcherオーケストレータ（<c>DebouncedWatcher</c>/<c>FileStabilityDetector</c>/
/// <c>PeriodicScanner</c>を束ねて<c>IWatcherService</c>を実装する、Phase2以降で追加予定のクラス）は、
/// 本インターフェースと<c>IWatcherService</c>の両方を実装し、
/// 内部で1個の「パス→(抑止解除時刻, トークン)」テーブルを共有する形を想定している
/// （<c>IWatcherService.SuppressPath</c>はこのテーブルへトークンなしで登録する薄いオーバーロードとして
/// 実装すればよい）。<see cref="FileOperationService"/>側は<see cref="IWatchSuppressor"/>のみに依存し、
/// Watcherの起動・停止・走査といった他の責務には関与しない（インターフェース分離）。
/// </para>
/// </remarks>
public interface IWatchSuppressor
{
    /// <summary>
    /// 指定パスへの今後の変更イベントを<paramref name="duration"/>の間抑止するようWatcher側へ要求する。
    /// ファイル操作（Move/Copy/Rename）の実行直前、シェル操作の呼び出しより先に呼ぶこと
    /// （操作完了後にイベントが飛んでくるより前に抑止登録を完了させ、取りこぼしを防ぐため）。
    /// </summary>
    /// <param name="path">今後の変更イベントを抑止する対象パス（通常は操作の移動/コピー/リネーム先）。</param>
    /// <param name="duration">抑止する期間。監視パイプライン（デバウンス+安定確認）が
    /// 完了しきるまでをカバーできる長さにする。</param>
    /// <param name="idempotencyToken">この操作を一意に識別するトークン（診断ログ・突合用）。</param>
    void SuppressPath(string path, TimeSpan duration, string idempotencyToken);
}
