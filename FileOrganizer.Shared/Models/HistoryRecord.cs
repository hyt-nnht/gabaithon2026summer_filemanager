namespace FileOrganizer.Shared.Models;

public enum OperationState
{
    Planned,
    Executing,
    Completed,
    Failed,
    Undoing,
    Undone,
    UndoFailed
}

public enum OperationType
{
    Move,
    Rename,
    Copy,
    Recycle
}

public class HistoryRecord
{
    public long Id { get; set; }
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public OperationType OpType { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string? DestinationPath { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime FileLastModifiedUtc { get; set; }
    public string LightweightHash { get; set; } = string.Empty;
    public OperationState State { get; set; } = OperationState.Planned;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
