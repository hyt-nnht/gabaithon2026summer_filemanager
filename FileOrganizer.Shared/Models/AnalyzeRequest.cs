using System.Text.Json.Serialization;

namespace FileOrganizer.Shared.Models;

/// <summary>
/// Python側 POST /api/v1/analyze へのリクエストDTO（AI_IMPLEMENTATION_GUIDE.md §3.2）。
/// </summary>
public class AnalyzeRequest
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("ocr_text")]
    public string? OcrText { get; set; }

    [JsonPropertyName("extract_fields")]
    public List<string> ExtractFields { get; set; } = new();
}
