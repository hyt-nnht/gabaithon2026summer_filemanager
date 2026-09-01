namespace FileOrganizer.Shared.Models;

/// <summary>
/// ルール評価対象ファイルの情報。
/// </summary>
public class FileMetadata
{
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime CreatedTimeUtc { get; set; }
    public double DaysOld => (DateTime.UtcNow - LastWriteTimeUtc).TotalDays;
    public string? OcrText { get; set; }        // OCR結果があれば設定（DB永続化しない前提）
    public string? AiCategory { get; set; }      // Python分析結果があれば設定
}
