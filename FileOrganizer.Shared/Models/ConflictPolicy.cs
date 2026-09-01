namespace FileOrganizer.Shared.Models;

/// <summary>
/// 同名衝突時の挙動。
/// </summary>
public enum ConflictPolicy
{
    AutoRename,
    Skip,
    PromptUser
}
