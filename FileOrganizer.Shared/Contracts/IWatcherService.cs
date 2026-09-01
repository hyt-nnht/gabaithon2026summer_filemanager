using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface IWatcherService
{
    event EventHandler<FileStableEventArgs>? FileStabilized;
    Task StartAsync(IEnumerable<WatchFolderSetting> folders, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task RescanAsync(CancellationToken ct = default); // 定期走査・BufferOverflow時の全体再走査用
    void SuppressPath(string path, TimeSpan duration); // 監視ループ防止（自アプリの移動先除外）
}
