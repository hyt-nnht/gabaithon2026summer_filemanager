using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileOrganizer.Core.Watcher;

/// <summary>
/// パス単位のデバウンス・重複排除ロジックのみを担う純粋なコア部分。
/// 実I/O・実時間待機を伴わないため、<see cref="Flush"/>を手動で呼び出すことで単体テストから
/// 決定的に検証できる。ファイルごとのTimer等は生成しない（追跡はすべて1個の
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>で管理する）。
/// </summary>
internal sealed class DebounceQueue
{
    private readonly TimeSpan _debounceWindow;
    private readonly ConcurrentDictionary<string, DateTime> _lastEventUtc = new(StringComparer.OrdinalIgnoreCase);

    public DebounceQueue(TimeSpan debounceWindow)
    {
        if (debounceWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), debounceWindow, "デバウンス期間は正の値である必要があります。");

        _debounceWindow = debounceWindow;
    }

    /// <summary>現在キューに滞留中（未確定）のパス数。</summary>
    public int PendingCount => _lastEventUtc.Count;

    /// <summary>
    /// パス単位の重複排除: 同一パスの再イベントは最終発生時刻を上書きするだけで、
    /// キューに複数エントリが積まれることはない。
    /// </summary>
    public void Enqueue(string path, DateTime eventUtc) => _lastEventUtc[path] = eventUtc;

    /// <summary>
    /// 最終イベントからデバウンス期間が経過したパスを確定させ、キューから取り除いて返す
    /// （まだ経過していないパスはキューに残り、次回の<see cref="Flush"/>で再評価される）。
    /// </summary>
    public IReadOnlyList<string> Flush(DateTime nowUtc)
    {
        List<string>? settled = null;
        foreach (var path in _lastEventUtc.Keys)
        {
            if (!_lastEventUtc.TryGetValue(path, out var last)) continue;
            if (nowUtc - last < _debounceWindow) continue;

            // 読み取り時点のタイムスタンプと完全一致する場合のみ条件付き削除。
            // 削除直前に新しいイベントが割り込んだ場合は削除せず次回に回す（取りこぼし防止）。
            if (((ICollection<KeyValuePair<string, DateTime>>)_lastEventUtc).Remove(new KeyValuePair<string, DateTime>(path, last)))
            {
                (settled ??= new List<string>()).Add(path);
            }
        }
        return (IReadOnlyList<string>?)settled ?? Array.Empty<string>();
    }
}

/// <summary>
/// 仕様書§3.4パイプライン図の第1〜2段（<c>FileSystemWatcher (Created/Changed)</c> →
/// <c>パス単位デバウンス&amp;重複排除キュー</c>）を実装する。<see cref="System.IO.FileSystemWatcher"/>の
/// 生イベントをラップし、短期間に連続発生する同一パスのイベントを1件に集約して
/// <see cref="SettledPaths"/>（<see cref="System.Threading.Channels.Channel{T}"/>）へ流す。
/// </summary>
/// <remarks>
/// デバウンスの確定処理はファイルごとにTimerを作るのではなく、単一の<see cref="PeriodicTimer"/>
/// ループが<see cref="DebounceQueue"/>を定期的に走査する方式で行う。これにより監視フォルダに
/// 1,000件規模のファイルが一括投入されても、生成されるバックグラウンドスレッドは
/// このループ用の1本のみに保たれる（仕様書§7.2-1）。
/// </remarks>
public sealed class DebouncedWatcher : IDisposable
{
    public const int DefaultDebounceMilliseconds = 300;
    public const int DefaultFlushIntervalMilliseconds = 100;

    private readonly FileSystemWatcher _watcher;
    private readonly DebounceQueue _queue;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoopTask;
    private readonly TimeSpan _flushInterval;
    private bool _disposed;

    /// <summary><see cref="FileSystemWatcher"/>がエラー（例: バッファオーバーフロー）を報告した際に発火する。</summary>
    public event EventHandler<ErrorEventArgs>? WatcherError;

    /// <summary>デバウンス確定後のパスを受け取るチャンネル読み取り口。</summary>
    public ChannelReader<string> SettledPaths => _channel.Reader;

    public string WatchFolder { get; }

    public DebouncedWatcher(
        string watchFolder,
        bool includeSubdirectories = false,
        TimeSpan? debounceWindow = null,
        TimeSpan? flushInterval = null)
    {
        if (string.IsNullOrWhiteSpace(watchFolder))
            throw new ArgumentException("watchFolderを空にすることはできません。", nameof(watchFolder));

        WatchFolder = watchFolder;
        _queue = new DebounceQueue(debounceWindow ?? TimeSpan.FromMilliseconds(DefaultDebounceMilliseconds));
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(DefaultFlushIntervalMilliseconds);

        _watcher = new FileSystemWatcher(watchFolder)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                            | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Created += OnRawEvent;
        _watcher.Changed += OnRawEvent;
        _watcher.Error += OnWatcherError;

        // ファイルごとのTimerではなく、単一の集約ループでデバウンスキューを定期フラッシュする。
        _flushLoopTask = Task.Run(() => FlushLoopAsync(_cts.Token));

        _watcher.EnableRaisingEvents = true;
    }

    private void OnRawEvent(object sender, FileSystemEventArgs e)
    {
        // サブフォルダ作成イベント等、ディレクトリ自体の変更は対象外（ファイルのみ監視）。
        if (Directory.Exists(e.FullPath)) return;

        _queue.Enqueue(e.FullPath, DateTime.UtcNow);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e) => WatcherError?.Invoke(this, e);

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var path in _queue.Flush(DateTime.UtcNow))
                {
                    await _channel.Writer.WriteAsync(path, ct).ConfigureAwait(false);
                }
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

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnRawEvent;
        _watcher.Changed -= OnRawEvent;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();

        _cts.Cancel();
        try
        {
            _flushLoopTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Dispose中の例外は無視（キャンセルによる正常終了を含む）。
        }
        _cts.Dispose();

        _channel.Writer.TryComplete();
    }
}
