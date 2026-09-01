using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface IUndoManager
{
    Task<UndoResult> UndoAsync(long historyRecordId, CancellationToken ct = default);
    Task<UndoResult> UndoAsync(string operationId, CancellationToken ct = default);
}
