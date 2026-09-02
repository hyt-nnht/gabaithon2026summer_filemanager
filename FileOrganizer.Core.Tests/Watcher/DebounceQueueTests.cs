using FileOrganizer.Core.Watcher;

namespace FileOrganizer.Core.Tests.Watcher;

/// <summary>
/// 仕様書§3.4「パス単位デバウンス&amp;重複排除キュー」のコアロジックを検証する。
/// 対象: <see cref="DebounceQueue"/>（実I/O・実時間待機を伴わない決定的テスト）。
/// </summary>
public class DebounceQueueTests
{
    private static readonly DateTime Base = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Enqueue_同一パスへの複数回呼び出しは1エントリに集約される()
    {
        var queue = new DebounceQueue(TimeSpan.FromMilliseconds(300));

        queue.Enqueue(@"C:\watch\a.txt", Base);
        queue.Enqueue(@"C:\watch\a.txt", Base.AddMilliseconds(50));
        queue.Enqueue(@"C:\watch\a.txt", Base.AddMilliseconds(100));

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void Flush_デバウンス期間未経過のパスは確定しない()
    {
        var queue = new DebounceQueue(TimeSpan.FromMilliseconds(300));
        queue.Enqueue(@"C:\watch\a.txt", Base);

        var settled = queue.Flush(Base.AddMilliseconds(100)); // まだ300ms経っていない

        Assert.Empty(settled);
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void Flush_デバウンス期間経過後は確定してキューから除去される()
    {
        var queue = new DebounceQueue(TimeSpan.FromMilliseconds(300));
        queue.Enqueue(@"C:\watch\a.txt", Base);

        var settled = queue.Flush(Base.AddMilliseconds(300));

        Assert.Equal(new[] { @"C:\watch\a.txt" }, settled);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Flush_確定後に再度Enqueueされたパスは新規イベントとして再度確定対象になる()
    {
        var queue = new DebounceQueue(TimeSpan.FromMilliseconds(300));
        queue.Enqueue(@"C:\watch\a.txt", Base);
        Assert.Single(queue.Flush(Base.AddMilliseconds(300)));

        queue.Enqueue(@"C:\watch\a.txt", Base.AddSeconds(1));
        Assert.Empty(queue.Flush(Base.AddSeconds(1) + TimeSpan.FromMilliseconds(100))); // 未経過
        Assert.Single(queue.Flush(Base.AddSeconds(1) + TimeSpan.FromMilliseconds(300))); // 経過後
    }

    [Fact]
    public void Flush_複数パスを独立して評価し経過済みのものだけ確定する()
    {
        var queue = new DebounceQueue(TimeSpan.FromMilliseconds(300));
        queue.Enqueue(@"C:\watch\old.txt", Base); // すぐ経過扱いになる
        queue.Enqueue(@"C:\watch\new.txt", Base.AddMilliseconds(250)); // まだ経過しない

        var settled = queue.Flush(Base.AddMilliseconds(300));

        Assert.Equal(new[] { @"C:\watch\old.txt" }, settled);
        Assert.Equal(1, queue.PendingCount); // new.txt は残る
    }

    [Fact]
    public void Flush_バースト書き込みは最後のイベントを基準にデバウンスされる()
    {
        // 100msごとに5回イベントが来ても、最終イベントから300ms経つまでは確定しない
        // → バーストが1件の確定に集約されることを確認する。
        var queue = new DebounceQueue(TimeSpan.FromMilliseconds(300));
        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(@"C:\watch\a.txt", Base.AddMilliseconds(i * 100));
        }
        var lastEventTime = Base.AddMilliseconds(4 * 100);

        Assert.Empty(queue.Flush(lastEventTime.AddMilliseconds(299)));
        Assert.Single(queue.Flush(lastEventTime.AddMilliseconds(300)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_デバウンス期間が0以下の場合は例外を投げる(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DebounceQueue(TimeSpan.FromMilliseconds(milliseconds)));
    }
}
