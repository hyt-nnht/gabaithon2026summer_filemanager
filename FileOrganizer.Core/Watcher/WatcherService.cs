using System.Collections.Concurrent;
using System.Threading.Channels;
using FileOrganizer.Core.Services;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Watcher;

/// <summary>
/// FileSystemWatcher、デバウンス、単一の静止判定ワーカー、定期走査を束ねる本番用サービス。
/// 起動時は既存ファイルを自動投入しない。既存ファイルはUIのDry Runか明示的な再走査で扱う。
/// </summary>
public sealed class WatcherService : IWatcherService, IWatchSuppressor, IAsyncDisposable
{
    private sealed record Suppression(DateTimeOffset ExpiresAt, string Token);

    private readonly int _stabilityCheckIntervalMs;
    private readonly int _periodicScanIntervalHours;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Suppression> _suppressions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _startupBaseline =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<DebouncedWatcher> _watchers = [];
    private readonly List<Task> _forwardingTasks = [];
    private CancellationTokenSource? _runCancellation;
    private FileStabilityDetector? _stabilityDetector;
    private PeriodicScanner? _periodicScanner;
    private bool _disposed;
    private int _suppressionInsertCount;

    public WatcherService(
        int stabilityCheckIntervalMs = FileStabilityDetector.DefaultStabilityCheckIntervalMs,
        int periodicScanIntervalHours = PeriodicScanner.DefaultPeriodicScanIntervalHours)
    {
        _stabilityCheckIntervalMs = stabilityCheckIntervalMs;
        _periodicScanIntervalHours = periodicScanIntervalHours;
    }

    public event EventHandler<FileStableEventArgs>? FileStabilized;

    public int PendingCount => (_stabilityDetector?.PendingCount ?? 0) + _watchers.Sum(watcher => watcher.PendingCount);
    public bool IsRunning => _runCancellation is not null;

    public async Task StartAsync(IEnumerable<WatchFolderSetting> folders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runCancellation is not null)
            {
                return;
            }

            List<WatchFolderSetting> activeFolders = folders
                .Where(folder => folder.Enabled &&
                    !string.IsNullOrWhiteSpace(folder.Path) && Directory.Exists(folder.Path))
                .Select(folder => new WatchFolderSetting
                {
                    Path = Path.GetFullPath(folder.Path),
                    Enabled = true,
                    IncludeSubdirectories = folder.IncludeSubdirectories,
                })
                .ToList();

            _runCancellation = new CancellationTokenSource();
            CaptureStartupBaseline(activeFolders, ct);
            _stabilityDetector = new FileStabilityDetector(_stabilityCheckIntervalMs);
            _stabilityDetector.FileStabilized += OnFileStabilized;

            // PeriodicScannerも抑止表を必ず通す。起動直後の全件走査は仕様上無効にする。
            _periodicScanner = new PeriodicScanner(
                new SuppressionAwareEnqueuer(this),
                activeFolders,
                _periodicScanIntervalHours,
                scanImmediatelyOnStart: false);

            CancellationToken runToken = _runCancellation.Token;
            foreach (WatchFolderSetting folder in activeFolders)
            {
                var watcher = new DebouncedWatcher(folder.Path, folder.IncludeSubdirectories);
                watcher.WatcherError += OnWatcherError;
                _watchers.Add(watcher);
                _forwardingTasks.Add(Task.Run(
                    () => ForwardAsync(watcher.SettledPaths, runToken),
                    runToken));
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cancellation = _runCancellation;
            _runCancellation = null;
            cancellation?.Cancel();

            foreach (DebouncedWatcher watcher in _watchers)
            {
                watcher.WatcherError -= OnWatcherError;
                watcher.Dispose();
            }
            _watchers.Clear();

            if (_forwardingTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(_forwardingTasks).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    // 終了時のキャンセル/タイムアウト。各所有オブジェクトはこの後確実に破棄する。
                }
                _forwardingTasks.Clear();
            }

            _periodicScanner?.Dispose();
            _periodicScanner = null;

            if (_stabilityDetector is not null)
            {
                _stabilityDetector.FileStabilized -= OnFileStabilized;
                _stabilityDetector.Dispose();
                _stabilityDetector = null;
            }

            cancellation?.Dispose();
            _suppressions.Clear();
            _startupBaseline.Clear();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        PeriodicScanner scanner = _periodicScanner
            ?? throw new InvalidOperationException("監視サービスは開始されていません。");
        await scanner.ScanNowAsync(ct).ConfigureAwait(false);
    }

    public void SuppressPath(string path, TimeSpan duration)
        => SuppressPath(path, duration, Guid.NewGuid().ToString("N"));

    public void SuppressPath(string path, TimeSpan duration, string idempotencyToken)
    {
        if (string.IsNullOrWhiteSpace(path) || duration <= TimeSpan.Zero)
        {
            return;
        }

        _suppressions[Normalize(path)] = new Suppression(DateTimeOffset.UtcNow.Add(duration), idempotencyToken);
        if ((Interlocked.Increment(ref _suppressionInsertCount) & 127) == 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string key, Suppression value) in _suppressions)
            {
                if (value.ExpiresAt <= now) _suppressions.TryRemove(key, out _);
            }
        }
    }

    private async Task ForwardAsync(ChannelReader<string> source, CancellationToken ct)
    {
        try
        {
            await foreach (string path in source.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // 起動時に存在したファイルでも、起動後にCreated/Changedが届いたものは新しい処理対象。
                _startupBaseline.TryRemove(Normalize(path), out _);
                if (!IsSuppressed(path))
                {
                    await (_stabilityDetector?.EnqueueAsync(path, ct) ?? ValueTask.CompletedTask).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsyncによる正常終了。
        }
    }

    private void OnFileStabilized(object? sender, FileStableEventArgs e)
    {
        if (!IsSuppressed(e.Metadata.FullPath))
        {
            FileStabilized?.Invoke(this, e);
        }
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
        => _periodicScanner?.TriggerImmediateRescan();

    private bool IsSuppressed(string path)
    {
        string normalized = Normalize(path);
        if (!_suppressions.TryGetValue(normalized, out Suppression? suppression))
        {
            return false;
        }

        if (suppression.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _suppressions.TryRemove(normalized, out _);
        return false;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    private void CaptureStartupBaseline(IEnumerable<WatchFolderSetting> folders, CancellationToken ct)
    {
        _startupBaseline.Clear();
        foreach (WatchFolderSetting folder in folders)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = folder.IncludeSubdirectories,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder.Path, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        _startupBaseline[Normalize(path)] = 0;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読める範囲だけを基準集合にする。監視開始そのものは継続する。
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycleLock.Dispose();
    }

    private sealed class SuppressionAwareEnqueuer(WatcherService owner) : IStabilityEnqueuer
    {
        public void Enqueue(string path)
        {
            if (!owner.IsSuppressed(path) && !owner._startupBaseline.ContainsKey(WatcherService.Normalize(path)))
                owner._stabilityDetector?.Enqueue(path);
        }

        public ValueTask EnqueueAsync(string path, CancellationToken ct = default)
            => owner.IsSuppressed(path) || owner._startupBaseline.ContainsKey(WatcherService.Normalize(path)) || owner._stabilityDetector is null
                ? ValueTask.CompletedTask
                : owner._stabilityDetector.EnqueueAsync(path, ct);
    }
}
