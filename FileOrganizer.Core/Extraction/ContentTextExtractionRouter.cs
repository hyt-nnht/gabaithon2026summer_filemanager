using FileOrganizer.Shared.Contracts;

namespace FileOrganizer.Core.Extraction;

/// <summary>
/// TXT/DOCXは本文を直接読み、それ以外は既存OCRへ委譲する本文抽出ルーター。
/// TXT/DOCX経路ではOCR言語パックを必要とせず、OCRを一切呼び出さない。
/// </summary>
public sealed class ContentTextExtractionRouter : IContentTextExtractor
{
    private readonly IOcrService _ocrService;
    private readonly IContentTextExtractor _plainTextExtractor;
    private readonly IContentTextExtractor _docxTextExtractor;

    public ContentTextExtractionRouter(
        IOcrService ocrService,
        IContentTextExtractor? plainTextExtractor = null,
        IContentTextExtractor? docxTextExtractor = null)
    {
        _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
        _plainTextExtractor = plainTextExtractor ?? new PlainTextExtractor();
        _docxTextExtractor = docxTextExtractor ?? new DocxTextExtractor();
    }

    public async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".txt" => await _plainTextExtractor.ExtractTextAsync(filePath, ct).ConfigureAwait(false),
            ".docx" => await _docxTextExtractor.ExtractTextAsync(filePath, ct).ConfigureAwait(false),
            _ => await ExtractWithOcrAsync(filePath, ct).ConfigureAwait(false),
        };
    }

    private async Task<string?> ExtractWithOcrAsync(string filePath, CancellationToken ct)
    {
        if (!await _ocrService.IsLanguagePackAvailableAsync().ConfigureAwait(false))
        {
            return null;
        }
        return await _ocrService.ExtractTextAsync(filePath, ct).ConfigureAwait(false);
    }
}
