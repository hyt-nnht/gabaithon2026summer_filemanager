using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>
/// Dry Runと実処理で共有するリネーム変数展開。
/// ファイル由来の変数は常に使え、AI由来の変数は解析成功時だけ置換する。
/// </summary>
public static class RenamePatternExpander
{
    public static string Expand(string pattern, string currentPath, AnalyzeResponse? analysis)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        string expanded = Replace(pattern, "ext", Path.GetExtension(currentPath));
        expanded = Replace(expanded, "filename", Path.GetFileNameWithoutExtension(currentPath));

        if (analysis is null)
        {
            return expanded;
        }

        expanded = Replace(expanded, "category", analysis.Category);
        foreach ((string key, string? value) in analysis.Metadata ?? new Dictionary<string, string>())
        {
            expanded = Replace(expanded, key, value);
        }

        return expanded;
    }

    private static string Replace(string source, string key, string? value)
        => string.IsNullOrEmpty(key) || value is null
            ? source
            : source.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
}
