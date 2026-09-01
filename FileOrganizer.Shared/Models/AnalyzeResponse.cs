using System.Text.Json.Serialization;

namespace FileOrganizer.Shared.Models;

/// <summary>
/// Python側 POST /api/v1/analyze からのレスポンスDTO（AI_IMPLEMENTATION_GUIDE.md §3.2）。
/// </summary>
public class AnalyzeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}
