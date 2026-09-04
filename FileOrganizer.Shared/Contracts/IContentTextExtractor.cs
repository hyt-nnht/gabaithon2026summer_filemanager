namespace FileOrganizer.Shared.Contracts;

/// <summary>
/// ファイル形式に応じて、分類・情報抽出へ渡す本文テキストを取得する。
/// 実装は直接読み込み（TXT/DOCX）またはOCR（PDF/画像）を選択できる。
/// </summary>
public interface IContentTextExtractor
{
    /// <summary>抽出できない形式・破損・読み込み失敗時は<c>null</c>を返す。</summary>
    Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default);
}
