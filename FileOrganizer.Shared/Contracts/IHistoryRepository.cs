using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface IHistoryRepository
{
    Task<long> InsertAsync(HistoryRecord record, CancellationToken ct = default);
    Task UpdateStateAsync(long id, OperationState newState, string? errorMessage = null, CancellationToken ct = default);
    Task<HistoryRecord?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<HistoryRecord?> GetByOperationIdAsync(string operationId, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryRecord>> GetRecordsByStateAsync(OperationState state, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int count, CancellationToken ct = default);
}
