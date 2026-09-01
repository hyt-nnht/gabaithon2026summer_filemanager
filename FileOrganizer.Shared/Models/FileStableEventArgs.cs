namespace FileOrganizer.Shared.Models;

/// <summary>
/// 監視サービスの安定検知イベント引数。
/// </summary>
public class FileStableEventArgs : EventArgs
{
    public FileMetadata Metadata { get; set; } = new();
    public string IdempotencyToken { get; set; } = Guid.NewGuid().ToString("N"); // 監視ループ防止用
}
