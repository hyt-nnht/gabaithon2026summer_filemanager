namespace FileOrganizer.Shared.Contracts;

public interface IOcrService : IContentTextExtractor
{
    Task<bool> IsLanguagePackAvailableAsync();
}
