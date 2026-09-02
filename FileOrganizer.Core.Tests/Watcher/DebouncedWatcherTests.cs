using FileOrganizer.Core.Watcher;

namespace FileOrganizer.Core.Tests.Watcher;

/// <summary>
/// 仕様書§3.4パイプライン図の第1〜2段を実際の<see cref="System.IO.FileSystemWatcher"/>込みで検証する
/// 統合テスト。対象: <see cref="DebouncedWatcher"/>。デバウンス期間・フラッシュ間隔は
/// テストを高速化するため短めに設定している。
/// </summary>
public class DebouncedWatcherTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "DebouncedWatcher", Guid.NewGuid().ToString("N"));

    public DebouncedWatcherTests()
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

    private static async Task<string> ReadOneAsync(DebouncedWatcher watcher, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await watcher.SettledPaths.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task ファイル作成後デバウンス期間が経過するとSettledPathsに1回だけ流れる()
    {
        using var watcher = new DebouncedWatcher(
            _workDir,
            debounceWindow: TimeSpan.FromMilliseconds(150),
            flushInterval: TimeSpan.FromMilliseconds(30));

        string filePath = Path.Combine(_workDir, "new-file.txt");
        File.WriteAllText(filePath, "hello");

        string settledPath = await ReadOneAsync(watcher, TimeSpan.FromSeconds(5));

        Assert.Equal(filePath, settledPath);
        Assert.False(watcher.SettledPaths.TryRead(out _)); // 重複配信されていない
    }

    [Fact]
    public async Task 同一ファイルへの連続書き込みバーストはデバウンスにより1回に集約される()
    {
        using var watcher = new DebouncedWatcher(
            _workDir,
            debounceWindow: TimeSpan.FromMilliseconds(200),
            flushInterval: TimeSpan.FromMilliseconds(30));

        string filePath = Path.Combine(_workDir, "burst.txt");

        // 短い間隔で複数回書き込み、デバウンス期間内はイベントが更新され続ける状況を再現する。
        for (int i = 0; i < 5; i++)
        {
            File.AppendAllText(filePath, $"line {i}\n");
            await Task.Delay(30);
        }

        string settledPath = await ReadOneAsync(watcher, TimeSpan.FromSeconds(5));
        Assert.Equal(filePath, settledPath);

        // バーストが1件に集約され、それ以上は流れてこないことを確認する。
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await watcher.SettledPaths.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task 別々のファイルはそれぞれ独立してSettledPathsに流れる()
    {
        using var watcher = new DebouncedWatcher(
            _workDir,
            debounceWindow: TimeSpan.FromMilliseconds(100),
            flushInterval: TimeSpan.FromMilliseconds(30));

        string pathA = Path.Combine(_workDir, "a.txt");
        string pathB = Path.Combine(_workDir, "b.txt");
        File.WriteAllText(pathA, "a");
        File.WriteAllText(pathB, "b");

        var settled = new HashSet<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        settled.Add(await watcher.SettledPaths.ReadAsync(cts.Token));
        settled.Add(await watcher.SettledPaths.ReadAsync(cts.Token));

        Assert.Contains(pathA, settled);
        Assert.Contains(pathB, settled);
    }

    [Fact]
    public void Dispose_例外を投げずにリソースを解放できる()
    {
        var watcher = new DebouncedWatcher(_workDir);
        var exception = Record.Exception(() => watcher.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_watchFolderが空文字の場合は例外を投げる()
    {
        Assert.Throws<ArgumentException>(() => new DebouncedWatcher(""));
    }
}
