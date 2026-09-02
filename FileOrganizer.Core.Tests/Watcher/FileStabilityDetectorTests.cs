using System.Collections.Concurrent;
using System.Linq;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Watcher;

/// <summary>
/// 仕様書§3.4パイプライン図の第3〜4段を実際の非同期ワーカーループ込みで検証する統合テスト。
/// 対象: <see cref="FileStabilityDetector"/>。実ファイルI/Oは<see cref="FakeFileProbe"/>で置き換え、
/// ポーリング間隔のみ短く設定してテストを高速化する
/// （<see cref="StabilityTracker"/>の判定ロジック自体は<c>StabilityTrackerTests</c>で個別検証済み）。
/// </summary>
public class FileStabilityDetectorTests
{
    private static readonly DateTime WriteTime = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CreateTime = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);

    private static FileSnapshot MakeSnapshot(long size = 100, FileAttributes attributes = FileAttributes.Normal)
        => new(size, WriteTime, CreateTime, attributes);

    private static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.True(ReferenceEquals(task, completed), "タイムアウトしました。");
        return await task;
    }

    [Fact]
    public async Task 単一ファイルが安定するとFileStabilizedイベントが正しいメタデータで発火する()
    {
        const string path = @"C:\watch\sample.pdf";
        var probe = new FakeFileProbe();
        probe.SetSnapshot(path, MakeSnapshot(size: 12345));

        using var detector = new FileStabilityDetector(stabilityCheckIntervalMs: 20, probe);
        var tcs = new TaskCompletionSource<FileStableEventArgs>();
        detector.FileStabilized += (_, e) => tcs.TrySetResult(e);

        detector.Enqueue(path);

        var args = await WaitAsync(tcs.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(path, args.Metadata.FullPath);
        Assert.Equal("sample.pdf", args.Metadata.FileName);
        Assert.Equal(".pdf", args.Metadata.Extension);
        Assert.Equal(12345, args.Metadata.SizeBytes);
        Assert.Equal(WriteTime, args.Metadata.LastWriteTimeUtc);
        Assert.False(string.IsNullOrEmpty(args.IdempotencyToken));
    }

    [Fact]
    public async Task ReparsePoint属性のファイルはFileStabilizedでなくFileExcludedが発火する()
    {
        const string path = @"C:\watch\onedrive-placeholder.pdf";
        var probe = new FakeFileProbe();
        probe.SetSnapshot(path, MakeSnapshot(attributes: FileAttributes.ReparsePoint));

        using var detector = new FileStabilityDetector(stabilityCheckIntervalMs: 20, probe);
        var excludedTcs = new TaskCompletionSource<string>();
        bool stabilizedFired = false;
        detector.FileExcluded += (_, excludedPath) => excludedTcs.TrySetResult(excludedPath);
        detector.FileStabilized += (_, _) => stabilizedFired = true;

        detector.Enqueue(path);

        string result = await WaitAsync(excludedTcs.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(path, result);
        Assert.False(stabilizedFired);
    }

    [Fact]
    public async Task Offline属性のファイルはFileStabilizedでなくFileExcludedが発火する()
    {
        const string path = @"C:\watch\onedrive-offline.pdf";
        var probe = new FakeFileProbe();
        probe.SetSnapshot(path, MakeSnapshot(attributes: FileAttributes.Offline));

        using var detector = new FileStabilityDetector(stabilityCheckIntervalMs: 20, probe);
        var excludedTcs = new TaskCompletionSource<string>();
        detector.FileExcluded += (_, excludedPath) => excludedTcs.TrySetResult(excludedPath);

        detector.Enqueue(path);

        string result = await WaitAsync(excludedTcs.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(path, result);
    }

    [Fact]
    public async Task Dispose後はワーカーループが停止し追加のイベントは発火しない()
    {
        const string path = @"C:\watch\sample.pdf";
        var probe = new FakeFileProbe();
        probe.SetSnapshot(path, MakeSnapshot());

        var detector = new FileStabilityDetector(stabilityCheckIntervalMs: 20, probe);
        int fireCount = 0;
        detector.FileStabilized += (_, _) => Interlocked.Increment(ref fireCount);

        detector.Dispose();
        detector.Enqueue(path); // Dispose後の投入は処理されないはず

        await Task.Delay(200); // ワーカーが誤動作していないことを確認するための猶予
        Assert.Equal(0, fireCount);
    }

    // --- 仕様書§7.2-1: 1,000件一括投入でもスレッド枯渇を起こさないこと -----------------------

    [Fact]
    public async Task Enqueue_1000件を一括投入してもスレッドプールを枯渇させず全件安定検知できる()
    {
        const int fileCount = 1000;
        var probe = new FakeFileProbe();
        var paths = new string[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            string path = $@"C:\watch\bulk-{i:D4}.pdf";
            paths[i] = path;
            probe.SetSnapshot(path, MakeSnapshot(size: 1000 + i));
        }

        using var detector = new FileStabilityDetector(stabilityCheckIntervalMs: 15, probe);

        var stabilizedPaths = new ConcurrentBag<string>();
        var allDone = new TaskCompletionSource();
        int remaining = fileCount;
        detector.FileStabilized += (_, e) =>
        {
            stabilizedPaths.Add(e.Metadata.FullPath);
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                allDone.TrySetResult();
            }
        };

        int threadCountBefore = ThreadPool.ThreadCount;
        int maxThreadCountObserved = threadCountBefore;

        using var samplerCts = new CancellationTokenSource();
        var samplerTask = Task.Run(async () =>
        {
            while (!samplerCts.IsCancellationRequested)
            {
                int current = ThreadPool.ThreadCount;
                if (current > maxThreadCountObserved) maxThreadCountObserved = current;
                try
                {
                    await Task.Delay(10, samplerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        // 1,000件を一括でキューへ投入（ファイルごとのTimer/Threadは生成されない設計）。
        foreach (var path in paths)
        {
            detector.Enqueue(path);
        }

        var completed = await Task.WhenAny(allDone.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        samplerCts.Cancel();
        try { await samplerTask; } catch { /* サンプラー停止時の例外は無視 */ }

        Assert.True(ReferenceEquals(allDone.Task, completed), "1000件の安定検知がタイムアウトしました。");
        Assert.Equal(fileCount, stabilizedPaths.Count);
        Assert.Equal(fileCount, stabilizedPaths.Distinct().Count());

        // ファイル単位でTimer/Threadを生成しない設計であることの確認（仕様書§7.2-1）。
        // 1ファイル1スレッドのアンチパターンであれば数百〜数千スレッドに達するはずだが、
        // 単一集約ワーカー設計であれば開始時とほぼ変わらない少数に留まる。
        Assert.True(maxThreadCountObserved < 100,
            $"ThreadPool.ThreadCount observed max={maxThreadCountObserved}（開始時={threadCountBefore}）。" +
            "ファイル単位でスレッドが生成されている疑いがあります。");
    }
}
