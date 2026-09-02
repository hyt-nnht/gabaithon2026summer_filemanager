using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Watcher;

/// <summary>
/// 安定検知パイプラインへパスを投入する操作の抽象化。<see cref="PeriodicScanner"/>等、
/// パス発生源となるコンポーネントが<see cref="FileStabilityDetector"/>本体に依存せず疎結合に
/// 連携できるようにするための境界（テストではフェイク実装で投入内容を検証できる）。
/// </summary>
public interface IStabilityEnqueuer
{
    void Enqueue(string path);
    ValueTask EnqueueAsync(string path, CancellationToken ct = default);
}

/// <summary>ファイルの現在の(サイズ・更新日時・属性)スナップショット取得を抽象化する。</summary>
/// <remarks>
/// <see cref="StabilityTracker"/>を実I/Oなしで決定的に単体テストできるようにするための境界。
/// 本番では<see cref="PhysicalFileProbe"/>を使う。
/// </remarks>
internal interface IFileProbe
{
    bool TryGetSnapshot(string path, out FileSnapshot snapshot);
}

internal readonly record struct FileSnapshot(
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    DateTime CreationTimeUtc,
    FileAttributes Attributes);

/// <summary><see cref="System.IO.FileInfo"/>ベースの既定実装。本番実行時はこちらを使用する。</summary>
internal sealed class PhysicalFileProbe : IFileProbe
{
    public bool TryGetSnapshot(string path, out FileSnapshot snapshot)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                snapshot = default;
                return false;
            }

            snapshot = new FileSnapshot(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes);
            return true;
        }
        catch (IOException)
        {
            // 排他ロック中等、一時的に取得できない場合は「未確定」として次回ポーリングで再試行する。
            snapshot = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            snapshot = default;
            return false;
        }
    }
}

internal enum StabilityPollOutcome
{
    StillPending,
    Stable,
    Excluded,
    Vanished,
}

internal readonly struct StabilityPollResult
{
    public string Path { get; }
    public StabilityPollOutcome Outcome { get; }
    public FileMetadata? Metadata { get; }

    private StabilityPollResult(string path, StabilityPollOutcome outcome, FileMetadata? metadata)
    {
        Path = path;
        Outcome = outcome;
        Metadata = metadata;
    }

    public static StabilityPollResult Stable(string path, FileMetadata metadata) => new(path, StabilityPollOutcome.Stable, metadata);
    public static StabilityPollResult Excluded(string path) => new(path, StabilityPollOutcome.Excluded, null);
    public static StabilityPollResult Vanished(string path) => new(path, StabilityPollOutcome.Vanished, null);
}

/// <summary>
/// 仕様書§3.4パイプライン図の第3〜4段
/// （<c>集約ポーリングワーカー(静止判定)</c> → <c>クラウド同期・特殊属性除外</c>）のコアロジック。
/// ファイルごとのTimerを一切使わず、追跡対象を1個の<see cref="ConcurrentDictionary{TKey,TValue}"/>に
/// まとめ、外部から定期的に呼び出される<see cref="PollOnce"/>のみで全件を1パス評価する。
/// 実I/Oは<see cref="IFileProbe"/>越しに行うため、単体テストではフェイク実装を注入して
/// 実時間待機・実ファイルなしに決定的に検証できる。
/// </summary>
internal sealed class StabilityTracker
{
    private sealed class Entry
    {
        public long? LastSize;
        public DateTime? LastWriteTimeUtc;
        public int ConsecutiveMatches;
    }

    private readonly IFileProbe _fileProbe;
    private readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);

    public StabilityTracker(IFileProbe fileProbe) => _fileProbe = fileProbe;

    public int PendingCount => _pending.Count;

    /// <summary>新規パスを追跡対象へ追加する（既に追跡中の場合は何もしない＝重複排除）。</summary>
    public void Track(string path) => _pending.TryAdd(path, new Entry());

    /// <summary>
    /// 追跡中の全パスを1回だけ再評価する（集約ポーリングワーカーの1ティック分）。
    /// サイズ・更新日時が前回ポーリング結果と2回連続で一致したパスをStableとして確定・追跡終了する。
    /// クラウド同期属性（ReparsePoint/Offline）を持つファイルは、確認を待たずに即座にExcludedとして
    /// 追跡終了する（仕様書§3.4「クラウド同期・特殊属性除外」＝未実体化ファイルの自動処理スキップ）。
    /// </summary>
    public IReadOnlyList<StabilityPollResult> PollOnce()
    {
        List<StabilityPollResult>? results = null;

        foreach (var path in _pending.Keys)
        {
            if (!_pending.TryGetValue(path, out var entry)) continue; // 既に他経路で除去済み

            if (!_fileProbe.TryGetSnapshot(path, out var snapshot))
            {
                // ファイルが消失（Undo・ユーザーによる削除等） → 追跡終了し、後段には渡さない。
                _pending.TryRemove(path, out _);
                (results ??= new List<StabilityPollResult>()).Add(StabilityPollResult.Vanished(path));
                continue;
            }

            if ((snapshot.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            {
                _pending.TryRemove(path, out _);
                (results ??= new List<StabilityPollResult>()).Add(StabilityPollResult.Excluded(path));
                continue;
            }

            if (entry.LastSize is long prevSize && entry.LastWriteTimeUtc is DateTime prevWrite
                && prevSize == snapshot.SizeBytes && prevWrite == snapshot.LastWriteTimeUtc)
            {
                entry.ConsecutiveMatches++;
                if (entry.ConsecutiveMatches >= 2)
                {
                    _pending.TryRemove(path, out _);
                    var metadata = new FileMetadata
                    {
                        FullPath = path,
                        FileName = Path.GetFileName(path),
                        Extension = Path.GetExtension(path),
                        SizeBytes = snapshot.SizeBytes,
                        LastWriteTimeUtc = snapshot.LastWriteTimeUtc,
                        CreatedTimeUtc = snapshot.CreationTimeUtc,
                    };
                    (results ??= new List<StabilityPollResult>()).Add(StabilityPollResult.Stable(path, metadata));
                }
            }
            else
            {
                // サイズ・更新日時に変化あり → 一致カウントをリセットし、新しい基準値を記録する。
                entry.LastSize = snapshot.SizeBytes;
                entry.LastWriteTimeUtc = snapshot.LastWriteTimeUtc;
                entry.ConsecutiveMatches = 0;
            }
        }

        return (IReadOnlyList<StabilityPollResult>?)results ?? Array.Empty<StabilityPollResult>();
    }
}

/// <summary>
/// 仕様書§3.4「単一ワーカーによる静止判定」を実装する集約ポーリングワーカー。
/// <see cref="DebouncedWatcher"/>（複数の監視フォルダ分あってよい）から届くパスを
/// <see cref="System.Threading.Channels.Channel{T}"/>経由で受け取り、<see cref="StabilityCheckIntervalMs"/>
/// （既定750ms、<c>AppSettings.StabilityCheckIntervalMs</c>と同値）間隔の単一
/// <see cref="PeriodicTimer"/>ループでサイズ・更新日時の一致を2回確認してから安定ファイルとして
/// <see cref="FileStabilized"/>イベントを発火する。ファイルごとのTimer/スレッドは一切生成しない。
/// </summary>
public sealed class FileStabilityDetector : IStabilityEnqueuer, IDisposable
{
    /// <summary>AppSettings.StabilityCheckIntervalMsの既定値（750ms）と同値。</summary>
    public const int DefaultStabilityCheckIntervalMs = 750;

    private readonly StabilityTracker _tracker;
    private readonly Channel<string> _inbox = Channel.CreateUnbounded<string>();
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly List<Task> _sourceForwardingTasks = new();
    private bool _disposed;

    /// <summary>サイズ・更新日時が2回連続一致し、安定ファイルとして確定した際に発火する。</summary>
    public event EventHandler<FileStableEventArgs>? FileStabilized;

    /// <summary>ReparsePoint/Offline属性により監視対象から除外されたパス（観測・ログ用、任意購読）。</summary>
    public event EventHandler<string>? FileExcluded;

    public int PendingCount => _tracker.PendingCount;

    public FileStabilityDetector(int stabilityCheckIntervalMs = DefaultStabilityCheckIntervalMs)
        : this(stabilityCheckIntervalMs, new PhysicalFileProbe())
    {
    }

    internal FileStabilityDetector(int stabilityCheckIntervalMs, IFileProbe fileProbe)
    {
        if (stabilityCheckIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(stabilityCheckIntervalMs), stabilityCheckIntervalMs, "間隔は正の値である必要があります。");

        _pollInterval = TimeSpan.FromMilliseconds(stabilityCheckIntervalMs);
        _tracker = new StabilityTracker(fileProbe);

        // 監視対象1,000件が一括投入されても、集約ワーカー用のこの1本のTaskのみで処理する
        // （ファイル単位のTimer/Taskを生成しない設計。仕様書§7.2-1）。
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    /// <summary>安定監視対象へパスを追加する（同期・ノンブロッキング）。</summary>
    public void Enqueue(string path) => _inbox.Writer.TryWrite(path);

    /// <summary>安定監視対象へパスを追加する（非同期）。</summary>
    public ValueTask EnqueueAsync(string path, CancellationToken ct = default)
        => _inbox.Writer.WriteAsync(path, ct);

    /// <summary>
    /// 別の発生源（例: 複数の<see cref="DebouncedWatcher"/>インスタンスの<see cref="DebouncedWatcher.SettledPaths"/>）
    /// からのパスをこの単一の集約ワーカーへ合流させる。複数の監視フォルダに対して
    /// FileStabilityDetectorを1個だけ共有する構成（真の意味での「集約」ワーカー）を可能にする。
    /// </summary>
    public void AttachSource(ChannelReader<string> source)
    {
        var forwardingTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var path in source.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    await EnqueueAsync(path, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose時の正常終了。
            }
        });

        lock (_sourceForwardingTasks)
        {
            _sourceForwardingTasks.Add(forwardingTask);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                DrainInbox();

                foreach (var result in _tracker.PollOnce())
                {
                    switch (result.Outcome)
                    {
                        case StabilityPollOutcome.Stable:
                            FileStabilized?.Invoke(this, new FileStableEventArgs { Metadata = result.Metadata! });
                            break;
                        case StabilityPollOutcome.Excluded:
                            FileExcluded?.Invoke(this, result.Path);
                            break;
                        case StabilityPollOutcome.Vanished:
                        case StabilityPollOutcome.StillPending:
                        default:
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose時の正常終了。
        }
    }

    private void DrainInbox()
    {
        while (_inbox.Reader.TryRead(out var path))
        {
            _tracker.Track(path);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _inbox.Writer.TryComplete();

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
