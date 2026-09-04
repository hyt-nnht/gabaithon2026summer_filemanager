using System.Text.Json.Serialization;

namespace FileOrganizer.Shared.Models;

/// <summary>
/// Python側 POST /api/v1/analyze へのリクエストDTO（AI_IMPLEMENTATION_GUIDE.md §3.2）。
/// </summary>
public class AnalyzeRequest
{
    public const int MaxOcrTextLength = 100_000;
    /// <summary>元ファイルの表示名・拡張子をPythonへ伝えるメタデータ。Pythonはこのパスを開かない。</summary>
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>C#でOCRまたはTXT/DOCXから直接抽出した本文。通常IPCでは必須。</summary>
    [JsonPropertyName("ocr_text")]
    public string OcrText { get; set; } = string.Empty;

    [JsonPropertyName("extract_fields")]
    public List<string> ExtractFields { get; set; } = new();
}
