namespace FileOrganizer.Shared.Contracts;

public interface IOcrService
{
    Task<bool> IsLanguagePackAvailableAsync();
    Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default); // 失敗時null（呼び出し元でgracefulフォールバック）
}
