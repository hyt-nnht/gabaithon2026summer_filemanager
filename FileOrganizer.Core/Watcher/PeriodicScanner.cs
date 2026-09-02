using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Watcher;

/// <summary>定期走査が1回完了した際の結果通知。</summary>
public sealed class PeriodicScanCompletedEventArgs : EventArgs
{
    public int EnqueuedFileCount { get; }
    public DateTime CompletedAtUtc { get; }

    public PeriodicScanCompletedEventArgs(int enqueuedFileCount, DateTime completedAtUtc)
    {
        EnqueuedFileCount = enqueuedFileCount;
        CompletedAtUtc = completedAtUtc;
    }
}

/// <summary>
/// 仕様書§3.4「イベント欠落対策」（<c>InternalBufferOverflowException</c>発生時、または定期走査時に
/// フォルダ全体を再スキャン）と、§3.1「定期走査」（ルールベース自動処理のトリガーの一つ）を実装する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.IO.FileSystemWatcher"/>はOSバッファ溢れ時にイベントを取りこぼす可能性があり
/// （<c>InternalBufferOverflowException</c>）、また「経過日数（<c>days_old</c>）」条件は
/// ファイル自体に変更イベントが一切発生しなくても時間経過だけで成立しうるため、
/// どちらもイベント駆動の監視だけでは検知できない。本クラスは
/// <c>AppSettings.PeriodicScanIntervalHours</c>（既定24時間）ごとに監視対象フォルダ全体を
/// 再列挙し、見つかった全ファイルを<see cref="IStabilityEnqueuer"/>（通常は
/// <see cref="FileStabilityDetector"/>）へ再投入することでこれを補う。新規・変更ファイルだけでなく
/// 既存の未変更ファイルも毎回投入するのは、<c>days_old</c>条件のルール再評価を機能させるために必須。
/// </para>
/// <para>
/// 走査はフォルダ数分のみのループであり、ファイルごとにTimer/Threadを生成することはない。
/// </para>
/// </remarks>
public sealed class PeriodicScanner : IDisposable
{
    /// <summary>AppSettings.PeriodicScanIntervalHoursの既定値（24時間）と同値。</summary>
    public const int DefaultPeriodicScanIntervalHours = 24;

    private readonly IStabilityEnqueuer _enqueuer;
    private readonly TimeSpan _scanInterval;
    private readonly object _foldersLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private List<WatchFolderSetting> _watchFolders;
    private CancellationTokenSource? _immediateTriggerCts;
    private bool _disposed;

    /// <summary>1回の走査（定期・即時いずれも）が完了するたびに発火する（観測・ログ用、任意購読）。</summary>
    public event EventHandler<PeriodicScanCompletedEventArgs>? ScanCompleted;

    /// <param name="enqueuer">
    /// 発見したファイルの投入先。通常は<see cref="FileStabilityDetector"/>を渡す
    /// （安定確認を経てから後段のルール評価へ進むため、走査結果を直接ルール評価へは渡さない）。
    /// </param>
    /// <param name="watchFolders">監視対象フォルダ設定（<c>AppSettings.WatchFolders</c>）。</param>
    /// <param name="periodicScanIntervalHours">走査間隔（時間）。既定は24時間。</param>
    /// <param name="scanImmediatelyOnStart">
    /// 構築直後に1回目の走査を即座に実行するか。既定はtrue（起動直後の取りこぼし・積み残しdays_old
    /// 条件をできるだけ早く拾うため）。falseにすると初回走査は<paramref name="periodicScanIntervalHours"/>
    /// 経過後まで行われない。
    /// </param>
    public PeriodicScanner(
        IStabilityEnqueuer enqueuer,
        IEnumerable<WatchFolderSetting> watchFolders,
        int periodicScanIntervalHours = DefaultPeriodicScanIntervalHours,
        bool scanImmediatelyOnStart = true)
    {
        _enqueuer = enqueuer ?? throw new ArgumentNullException(nameof(enqueuer));
        if (watchFolders is null) throw new ArgumentNullException(nameof(watchFolders));
        if (periodicScanIntervalHours <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodicScanIntervalHours), periodicScanIntervalHours, "走査間隔は正の値である必要があります。");

        _watchFolders = new List<WatchFolderSetting>(watchFolders);
        _scanInterval = TimeSpan.FromHours(periodicScanIntervalHours);

        // 集約ワーカー用の1本のTaskのみを生成する（フォルダ数・ファイル数に依存してTimer/Threadを増やさない）。
        _workerTask = Task.Run(() => WorkerLoopAsync(scanImmediatelyOnStart, _cts.Token));
    }

    /// <summary>監視対象フォルダ設定を更新する（次回走査から反映される）。</summary>
    public void UpdateWatchFolders(IEnumerable<WatchFolderSetting> watchFolders)
    {
        if (watchFolders is null) throw new ArgumentNullException(nameof(watchFolders));
        lock (_foldersLock)
        {
            _watchFolders = new List<WatchFolderSetting>(watchFolders);
        }
    }

    /// <summary>
    /// 次回の定期走査を待たず、即座に再走査を要求する。
    /// <see cref="System.IO.FileSystemWatcher"/>の<c>InternalBufferOverflowException</c>発生時に、
    /// そのフォルダの監視主体（<see cref="DebouncedWatcher.WatcherError"/>等）から呼び出す想定。
    /// </summary>
    public void TriggerImmediateRescan()
    {
        // 待機中のワーカーを起こす。スキャン実行中（_immediateTriggerCtsがnull）の呼び出しは、
        // 実行中または直前に完了した走査が既に全ファイルを再投入済みのため、実質的に不要であり無視してよい。
        _immediateTriggerCts?.Cancel();
    }

    /// <summary>
    /// 監視対象フォルダ全体を1回走査し、対象ファイルすべてを安定検知パイプラインへ再投入する。
    /// 定期実行・即時トリガーの双方から、また外部（テスト等）からも直接呼び出せる。
    /// </summary>
    public async Task<int> ScanNowAsync(CancellationToken ct = default)
    {
        List<WatchFolderSetting> folders;
        lock (_foldersLock)
        {
            folders = _watchFolders;
        }

        int enqueuedCount = 0;
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            if (!folder.Enabled) continue;
            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path)) continue;

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = folder.IncludeSubdirectories,
                // 隠しファイル・システムファイル（既定でHidden|Systemがスキップ対象）に加え、
                // シンボリックリンク/ジャンクション（ReparsePoint）も列挙対象外にする
                // （仕様書§6「対象外ファイル」。再帰走査時にジャンクション経由でループ・意図しない
                // 領域へ入り込むことも防ぐ）。
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path, "*", enumerationOptions);
            }
            catch (IOException)
            {
                continue; // 走査中にフォルダ自体が消えた等 → このフォルダはスキップし他フォルダを継続
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                // ショートカット（.lnk）は仕様書§6により監視・処理から既定で除外。
                if (string.Equals(Path.GetExtension(filePath), ".lnk", StringComparison.OrdinalIgnoreCase))
                    continue;

                await _enqueuer.EnqueueAsync(filePath, ct).ConfigureAwait(false);
                enqueuedCount++;
            }
        }

        ScanCompleted?.Invoke(this, new PeriodicScanCompletedEventArgs(enqueuedCount, DateTime.UtcNow));
        return enqueuedCount;
    }

    private async Task WorkerLoopAsync(bool scanImmediatelyOnStart, CancellationToken stopToken)
    {
        try
        {
            if (scanImmediatelyOnStart)
            {
                await ScanNowAsync(stopToken).ConfigureAwait(false);
            }

            while (!stopToken.IsCancellationRequested)
            {
                using var triggerCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                _immediateTriggerCts = triggerCts;
                try
                {
                    await Task.Delay(_scanInterval, triggerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
                {
                    // TriggerImmediateRescanによる早期起床。定期スケジュールを待たず再走査へ進む。
                }
                finally
                {
                    _immediateTriggerCts = null;
                }

                if (stopToken.IsCancellationRequested) break;

                await ScanNowAsync(stopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose時の正常終了。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Dispose中の例外は無視（キャンセルによる正常終了を含む）。
        }
        _cts.Dispose();
    }
}
