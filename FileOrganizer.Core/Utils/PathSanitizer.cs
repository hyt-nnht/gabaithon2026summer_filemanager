using System;
using System.Collections.Generic;
using System.IO;

namespace FileOrganizer.Core.Utils;

/// <summary>
/// AI_IMPLEMENTATION_GUIDE.md §5.1準拠のファイル名サニタイザ。
/// 仕様書§6「ファイル名サニタイズ（末尾ドット問題の解決）」を満たすため、
/// リネーム/移動の直前に必ずこの<see cref="SanitizeFileName"/>を通すこと。
/// - 禁止文字（<c>\ / : * ? " &lt; &gt; |</c>）を <c>_</c> に置換
/// - Windows予約デバイス名（<c>CON</c>, <c>NUL</c> 等、大文字小文字を問わない）に <c>_file</c> を付与
/// - ファイル名本体・拡張子双方の末尾から <c>.</c> と半角スペースを除去し、パス解決の破綻を防止
/// - 上記処理の結果として空文字列になる場合は <c>renamed_file</c> にフォールバック
/// </summary>
public static class PathSanitizer
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    // Windows予約デバイス名（大文字小文字問わず判定）
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeFileName(string rawFileName)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(rawFileName);
        string ext = Path.GetExtension(rawFileName); // 例: ".pdf" または ""

        // 1. 不正文字（\ / : * ? " < > |）を置換
        foreach (char c in InvalidChars)
        {
            nameWithoutExt = nameWithoutExt.Replace(c, '_');
        }

        // 2. ファイル名本体の末尾から '.' と ' ' を除去
        nameWithoutExt = nameWithoutExt.TrimEnd('.', ' ');

        // 3. 拡張子から先頭ドットおよび末尾の空白等を除去
        string cleanExt = ext.TrimStart('.').TrimEnd('.', ' ');

        // 4. 空文字列判定
        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            nameWithoutExt = "renamed_file";

        // 5. Windows予約デバイス名の完全一致チェック（例: CON -> CON_file）
        if (ReservedDeviceNames.Contains(nameWithoutExt))
        {
            nameWithoutExt = $"{nameWithoutExt}_file";
        }

        // 6. 二重ドット（sample..pdf）を防ぎ結合
        return string.IsNullOrEmpty(cleanExt) ? nameWithoutExt : $"{nameWithoutExt}.{cleanExt}";
    }
}
