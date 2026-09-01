namespace FileOrganizer.Shared.Models;

/// <summary>
/// ファイル操作結果。
/// </summary>
public class OperationResult
{
    public bool Success { get; set; }
    public string? FinalPath { get; set; }       // 連番付与後の実際のパス等
    public string? ErrorMessage { get; set; }
    public bool WasSkippedDueToConflict { get; set; }
}
