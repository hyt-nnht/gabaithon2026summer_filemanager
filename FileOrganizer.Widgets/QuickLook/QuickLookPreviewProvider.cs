using System.IO;

namespace FileOrganizer.Widgets.QuickLook;

public sealed class QuickLookPreviewProvider
{
    private const int MaxPreviewCharacters = 64 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".log", ".cs", ".py", ".js", ".ts", ".html", ".css"
    };

    public async Task<QuickLookPresentation> CreateAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("プレビュー対象が見つかりません。", path);

        string extension = info.Extension;
        string preview;
        string kind;
        if (TextExtensions.Contains(extension))
        {
            kind = "テキスト";
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            // 上限より1文字だけ多く非同期で読み、同期的なEndOfStream参照をせずに
            // 「末尾が省略されたか」を判定する（CA2024対策）。
            char[] buffer = new char[MaxPreviewCharacters + 1];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            int previewLength = Math.Min(read, MaxPreviewCharacters);
            preview = new string(buffer, 0, previewLength);
            if (read > MaxPreviewCharacters) preview += "\n\n…（先頭64K文字まで表示）";
        }
        else
        {
            kind = string.IsNullOrWhiteSpace(extension) ? "ファイル" : $"{extension.TrimStart('.').ToUpperInvariant()} ファイル";
            preview = "この形式は内容のテキスト表示に対応していません。\nアプリで開く前に、名前・場所・サイズ・更新日時を確認できます。";
        }

        return new QuickLookPresentation(
            info.Name,
            info.FullName,
            string.IsNullOrWhiteSpace(extension) ? "F" : extension.TrimStart('.').ToUpperInvariant()[..Math.Min(3, extension.TrimStart('.').Length)],
            kind,
            $"{FormatSize(info.Length)}  •  更新 {info.LastWriteTime:g}",
            preview);
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB"
         : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB"
         : $"{bytes} B";
}
