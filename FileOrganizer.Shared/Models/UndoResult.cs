namespace FileOrganizer.Shared.Models;

/// <summary>
/// Undo結果。
/// </summary>
public enum UndoOutcome
{
    Success,
    RequiresConfirmation,
    Failed
}

public class UndoResult
{
    public UndoOutcome Outcome { get; set; }
    public string? Message { get; set; }
}
