using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface IFileOperationService
{
    Task<OperationResult> MoveAsync(string sourcePath, string destinationDirectory, ConflictPolicy policy, CancellationToken ct = default);
    Task<OperationResult> CopyAsync(string sourcePath, string destinationDirectory, ConflictPolicy policy, CancellationToken ct = default);
    Task<OperationResult> RenameAsync(string sourcePath, string newFileName, CancellationToken ct = default);
    Task<OperationResult> RecycleAsync(string sourcePath, CancellationToken ct = default);
}
