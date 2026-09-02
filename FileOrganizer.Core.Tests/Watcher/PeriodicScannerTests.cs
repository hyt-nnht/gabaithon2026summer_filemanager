using System.Collections.Concurrent;
using System.Linq;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Watcher;

/// <summary>
/// <see cref="IStabilityEnqueuer"/>のフェイク実装。実際の<see cref="FileStabilityDetector"/>を
/// 起動せず、<see cref="PeriodicScanner"/>がどのパスを何回投入したかだけを記録する。
/// </summary>
internal sealed class FakeStabilityEnqueuer : IStabilityEnqueuer
{
    private readonly ConcurrentQueue<string> _enqueued = new();

    public IReadOnlyList<string> EnqueuedPaths => _enqueued.ToArray();

    public void Enqueue(string path) => _enqueued.Enqueue(path);

    public ValueTask EnqueueAsync(string path, CancellationToken ct = default)
    {
        _enqueued.Enqueue(path);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 仕様書§3.4「イベント欠落対策」（<c>InternalBufferOverflowException</c>時・定期走査時の全体再スキャン）と、
/// §3.1「定期走査」（経過日数条件を含むルール評価のトリガー）の受け入れ基準を検証する。
/// 対象: <see cref="PeriodicScanner"/>。
/// </summary>
public class PeriodicScannerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "PeriodicScanner", Guid.NewGuid().ToString("N"));

    public PeriodicScannerTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの後始末失敗は無視。
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "条件がタイムアウト内に満たされませんでした。");
    }

    // --- ScanNowAsync: 列挙・除外ルール ------------------------------------------------

    [Fact]
    public async Task ScanNowAsync_フォルダ直下の全ファイルをEnqueueする()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.pdf"), "a");
        File.WriteAllText(Path.Combine(_workDir, "b.txt"), "b");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true, IncludeSubdirectories = false } };
        using var scanner = new PeriodicScanner(enqueuer, folders, periodicScanIntervalHours: 24, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(2, count);
        Assert.Contains(Path.Combine(_workDir, "a.pdf"), enqueuer.EnqueuedPaths);
        Assert.Contains(Path.Combine(_workDir, "b.txt"), enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task ScanNowAsync_IncludeSubdirectoriesがtrueならサブフォルダも走査する()
    {
        string subDir = Path.Combine(_workDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_workDir, "top.txt"), "top");
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true, IncludeSubdirectories = true } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(2, count);
        Assert.Contains(Path.Combine(subDir, "nested.txt"), enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task ScanNowAsync_IncludeSubdirectoriesがfalseならサブフォルダは走査しない()
    {
        string subDir = Path.Combine(_workDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_workDir, "top.txt"), "top");
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true, IncludeSubdirectories = false } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(1, count);
        Assert.DoesNotContain(Path.Combine(subDir, "nested.txt"), enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task ScanNowAsync_Enabledがfalseのフォルダはスキップする()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "a");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = false, IncludeSubdirectories = false } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(0, count);
        Assert.Empty(enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task ScanNowAsync_存在しないフォルダは例外を投げずスキップする()
    {
        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[]
        {
            new WatchFolderSetting { Path = Path.Combine(_workDir, "does-not-exist"), Enabled = true },
        };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ScanNowAsync_lnkショートカットは除外する()
    {
        File.WriteAllText(Path.Combine(_workDir, "real.txt"), "real");
        File.WriteAllText(Path.Combine(_workDir, "shortcut.lnk"), "not a real shortcut but same extension");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(1, count);
        Assert.DoesNotContain(enqueuer.EnqueuedPaths, p => p.EndsWith(".lnk"));
    }

    [Fact]
    public async Task ScanNowAsync_隠しファイル_システムファイルは除外する()
    {
        string hiddenPath = Path.Combine(_workDir, "hidden.txt");
        string systemPath = Path.Combine(_workDir, "system.txt");
        string normalPath = Path.Combine(_workDir, "normal.txt");
        File.WriteAllText(hiddenPath, "h");
        File.WriteAllText(systemPath, "s");
        File.WriteAllText(normalPath, "n");
        File.SetAttributes(hiddenPath, FileAttributes.Hidden);
        File.SetAttributes(systemPath, FileAttributes.System);

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(1, count);
        Assert.Contains(normalPath, enqueuer.EnqueuedPaths);
        Assert.DoesNotContain(hiddenPath, enqueuer.EnqueuedPaths);
        Assert.DoesNotContain(systemPath, enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task ScanNowAsync_複数フォルダを合算して走査する()
    {
        string folderA = Path.Combine(_workDir, "a");
        string folderB = Path.Combine(_workDir, "b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        File.WriteAllText(Path.Combine(folderA, "1.txt"), "1");
        File.WriteAllText(Path.Combine(folderB, "2.txt"), "2");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[]
        {
            new WatchFolderSetting { Path = folderA, Enabled = true },
            new WatchFolderSetting { Path = folderB, Enabled = true },
        };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        int count = await scanner.ScanNowAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ScanCompleted_走査完了後に投入件数付きで発火する()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "a");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        PeriodicScanCompletedEventArgs? received = null;
        scanner.ScanCompleted += (_, e) => received = e;

        await scanner.ScanNowAsync();

        Assert.NotNull(received);
        Assert.Equal(1, received!.EnqueuedFileCount);
    }

    // --- 経過日数(days_old)ルール評価のトリガーとして: 未変更ファイルも毎回再投入される ------

    [Fact]
    public async Task ScanNowAsync_変化のない既存ファイルも走査のたびに再投入される()
    {
        // days_old条件はファイル自体の変更イベントなしに時間経過だけで成立しうるため、
        // 定期走査は「未変更」のファイルも毎回再投入できる必要がある（内容・属性を一切変えていない）。
        string filePath = Path.Combine(_workDir, "stale.pdf");
        File.WriteAllText(filePath, "unchanged content");

        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };
        using var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        await scanner.ScanNowAsync();
        await scanner.ScanNowAsync();

        int occurrences = enqueuer.EnqueuedPaths.Count(p => p == filePath);
        Assert.Equal(2, occurrences);
    }

    // --- UpdateWatchFolders ----------------------------------------------------------

    [Fact]
    public async Task UpdateWatchFolders_更新後の走査に新しいフォルダ設定が反映される()
    {
        string folderA = Path.Combine(_workDir, "a");
        string folderB = Path.Combine(_workDir, "b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        File.WriteAllText(Path.Combine(folderA, "1.txt"), "1");
        File.WriteAllText(Path.Combine(folderB, "2.txt"), "2");

        var enqueuer = new FakeStabilityEnqueuer();
        var initialFolders = new[] { new WatchFolderSetting { Path = folderA, Enabled = true } };
        using var scanner = new PeriodicScanner(enqueuer, initialFolders, scanImmediatelyOnStart: false);

        Assert.Equal(1, await scanner.ScanNowAsync());

        scanner.UpdateWatchFolders(new[] { new WatchFolderSetting { Path = folderB, Enabled = true } });
        int secondCount = await scanner.ScanNowAsync();

        Assert.Equal(1, secondCount);
        Assert.Contains(Path.Combine(folderB, "2.txt"), enqueuer.EnqueuedPaths);
    }

    // --- 定期実行・即時トリガー（イベント欠落対策） -------------------------------------

    [Fact]
    public async Task Constructor_scanImmediatelyOnStartがtrueなら起動直後に1回走査される()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "a");
        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };

        using var scanner = new PeriodicScanner(enqueuer, folders, periodicScanIntervalHours: 24, scanImmediatelyOnStart: true);

        await WaitUntilAsync(() => enqueuer.EnqueuedPaths.Count > 0, TimeSpan.FromSeconds(5));
        Assert.Single(enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task TriggerImmediateRescan_定期スケジュールを待たず即座に再走査される()
    {
        // InternalBufferOverflowException相当のシナリオ: 長い定期間隔でも、即時トリガーにより
        // 短時間で再走査が実行されることを確認する。
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "a");
        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };

        // periodicScanIntervalHoursの最小単位は時間だが、内部的にTimeSpan.FromHours()で保持されるため
        // ここでは非常に長い間隔（24時間）を設定し、トリガーなしでは即座に走査されないことも併せて確認する。
        using var scanner = new PeriodicScanner(enqueuer, folders, periodicScanIntervalHours: 24, scanImmediatelyOnStart: false);

        await Task.Delay(200);
        Assert.Empty(enqueuer.EnqueuedPaths); // トリガーなしではまだ走査されていない

        scanner.TriggerImmediateRescan();

        await WaitUntilAsync(() => enqueuer.EnqueuedPaths.Count > 0, TimeSpan.FromSeconds(5));
        Assert.Single(enqueuer.EnqueuedPaths);
    }

    [Fact]
    public async Task TriggerImmediateRescan_開始前_停止後に呼んでも例外を投げない()
    {
        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };
        var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        scanner.Dispose();
        var exception = Record.Exception(() => scanner.TriggerImmediateRescan());

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_例外を投げずに停止できる()
    {
        var enqueuer = new FakeStabilityEnqueuer();
        var folders = new[] { new WatchFolderSetting { Path = _workDir, Enabled = true } };
        var scanner = new PeriodicScanner(enqueuer, folders, scanImmediatelyOnStart: false);

        var exception = Record.Exception(() => scanner.Dispose());
        Assert.Null(exception);
    }

    // --- コンストラクタ引数検証 ---------------------------------------------------------

    [Fact]
    public void Constructor_enqueuerがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PeriodicScanner(null!, Array.Empty<WatchFolderSetting>(), scanImmediatelyOnStart: false));
    }

    [Fact]
    public void Constructor_watchFoldersがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PeriodicScanner(new FakeStabilityEnqueuer(), null!, scanImmediatelyOnStart: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_periodicScanIntervalHoursが0以下の場合は例外を投げる(int hours)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PeriodicScanner(new FakeStabilityEnqueuer(), Array.Empty<WatchFolderSetting>(), periodicScanIntervalHours: hours, scanImmediatelyOnStart: false));
    }
}
