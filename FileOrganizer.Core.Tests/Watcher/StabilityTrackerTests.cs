using System.Collections.Concurrent;
using FileOrganizer.Core.Watcher;

namespace FileOrganizer.Core.Tests.Watcher;

/// <summary>
/// <see cref="StabilityTracker"/>用のフェイク<see cref="IFileProbe"/>。実I/Oなしで
/// 任意の(サイズ・更新日時・属性)スナップショットをテストから直接制御できる。
/// </summary>
internal sealed class FakeFileProbe : IFileProbe
{
    private readonly ConcurrentDictionary<string, FileSnapshot?> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public void SetSnapshot(string path, FileSnapshot snapshot) => _snapshots[path] = snapshot;

    public void Remove(string path) => _snapshots.TryRemove(path, out _);

    public bool TryGetSnapshot(string path, out FileSnapshot snapshot)
    {
        if (_snapshots.TryGetValue(path, out var maybe) && maybe is FileSnapshot s)
        {
            snapshot = s;
            return true;
        }
        snapshot = default;
        return false;
    }
}

/// <summary>
/// 仕様書§3.4「集約ポーリングワーカー(静止判定)」「クラウド同期・特殊属性除外」の
/// コアロジックを検証する。対象: <see cref="StabilityTracker"/>。
/// フェイク<see cref="IFileProbe"/>により実ファイル・実時間待機なしで<see cref="StabilityTracker.PollOnce"/>を
/// 明示的に複数回呼び出し、集約ポーリングワーカーの1ティックずつを決定的に再現する。
/// </summary>
public class StabilityTrackerTests
{
    private const string Path = @"C:\watch\sample.pdf";
    private static readonly DateTime WriteTime = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    private static FileSnapshot MakeSnapshot(long size = 100, DateTime? writeTime = null, FileAttributes attributes = FileAttributes.Normal)
        => new(size, writeTime ?? WriteTime, WriteTime, attributes);

    [Fact]
    public void PollOnce_初回ポーリングは基準値記録のみでStableにならない()
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot());
        var tracker = new StabilityTracker(probe);
        tracker.Track(Path);

        var results = tracker.PollOnce();

        Assert.Empty(results);
        Assert.Equal(1, tracker.PendingCount); // まだ追跡継続中
    }

    [Fact]
    public void PollOnce_サイズ_更新日時が2回連続一致するとStableとして確定する()
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot());
        var tracker = new StabilityTracker(probe);
        tracker.Track(Path);

        Assert.Empty(tracker.PollOnce()); // 1回目: 基準値記録
        Assert.Empty(tracker.PollOnce()); // 2回目: 1致目の一致確認
        var results = tracker.PollOnce(); // 3回目: 2致目の一致確認 → Stable確定

        var result = Assert.Single(results);
        Assert.Equal(StabilityPollOutcome.Stable, result.Outcome);
        Assert.Equal(Path, result.Path);
        Assert.NotNull(result.Metadata);
        Assert.Equal(100, result.Metadata!.SizeBytes);
        Assert.Equal("sample.pdf", result.Metadata.FileName);
        Assert.Equal(".pdf", result.Metadata.Extension);
        Assert.Equal(0, tracker.PendingCount); // 追跡終了
    }

    [Fact]
    public void PollOnce_ポーリング間でサイズが変化すると一致カウントがリセットされる()
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot(size: 100));
        var tracker = new StabilityTracker(probe);
        tracker.Track(Path);

        Assert.Empty(tracker.PollOnce()); // 基準値: size=100
        Assert.Empty(tracker.PollOnce()); // 一致1回目

        probe.SetSnapshot(Path, MakeSnapshot(size: 200)); // 書き込み継続中（サイズ増加）
        Assert.Empty(tracker.PollOnce()); // 変化検知 → カウントリセット、新基準値=200

        Assert.Empty(tracker.PollOnce()); // 一致1回目（200基準）
        var results = tracker.PollOnce(); // 一致2回目 → Stable確定

        var result = Assert.Single(results);
        Assert.Equal(StabilityPollOutcome.Stable, result.Outcome);
        Assert.Equal(200, result.Metadata!.SizeBytes);
    }

    [Fact]
    public void PollOnce_ファイルが消失した場合はVanishedとして追跡終了する()
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot());
        var tracker = new StabilityTracker(probe);
        tracker.Track(Path);
        tracker.PollOnce(); // 基準値記録

        probe.Remove(Path); // 削除・Undo等で消失
        var results = tracker.PollOnce();

        var result = Assert.Single(results);
        Assert.Equal(StabilityPollOutcome.Vanished, result.Outcome);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Offline)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Offline)]
    public void PollOnce_ReparsePointまたはOffline属性は一致確認を待たず即座にExcludedになる(FileAttributes attributes)
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot(attributes: attributes));
        var tracker = new StabilityTracker(probe);
        tracker.Track(Path);

        var results = tracker.PollOnce(); // 初回ポーリングで即座に除外

        var result = Assert.Single(results);
        Assert.Equal(StabilityPollOutcome.Excluded, result.Outcome);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void PollOnce_通常属性のファイルはExcludedにならない()
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot(attributes: FileAttributes.Normal));
        var tracker = new StabilityTracker(probe);
        tracker.Track(Path);

        tracker.PollOnce();
        tracker.PollOnce();
        var results = tracker.PollOnce();

        Assert.Equal(StabilityPollOutcome.Stable, Assert.Single(results).Outcome);
    }

    [Fact]
    public void PollOnce_複数パスを独立して追跡できる()
    {
        const string pathA = @"C:\watch\a.pdf";
        const string pathB = @"C:\watch\b.pdf";
        var probe = new FakeFileProbe();
        probe.SetSnapshot(pathA, MakeSnapshot(size: 10));
        probe.SetSnapshot(pathB, MakeSnapshot(size: 20));
        var tracker = new StabilityTracker(probe);
        tracker.Track(pathA);
        tracker.Track(pathB);

        tracker.PollOnce(); // 両方: 基準値記録
        tracker.PollOnce(); // 両方: 一致1回目

        // pathBだけ書き込み継続中（サイズ変化）→ pathAのみ確定するはず
        probe.SetSnapshot(pathB, MakeSnapshot(size: 30));
        var results = tracker.PollOnce();

        var result = Assert.Single(results);
        Assert.Equal(pathA, result.Path);
        Assert.Equal(StabilityPollOutcome.Stable, result.Outcome);
        Assert.Equal(1, tracker.PendingCount); // pathBはまだ追跡継続中
    }

    [Fact]
    public void Track_同一パスの複数回呼び出しは重複排除される()
    {
        var probe = new FakeFileProbe();
        probe.SetSnapshot(Path, MakeSnapshot());
        var tracker = new StabilityTracker(probe);

        tracker.Track(Path);
        tracker.Track(Path);
        tracker.Track(Path);

        Assert.Equal(1, tracker.PendingCount);
    }
}
