using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FileOrganizer.Core.Utils;

/// <summary>
/// 仕様書§7.2-7「診断ログ: ワンクリック出力されるサポートログは、ファイル名・個人情報が
/// ハッシュ化/マスクされていること」のコアロジック。<c>FileOrganizer.Widgets</c>の
/// <c>SupportLogExporter</c>（ワンクリック診断ログ出力機能）から呼び出される想定で、
/// UIに依存しないロジックのみを<c>FileOrganizer.Core</c>側に切り出している。
/// </summary>
/// <remarks>
/// 【重要】CLAUDE.mdの「プライバシー」ルール（OCR抽出テキスト全文はDB・ログに永続化しない）に基づき、
/// 本クラスは「OCR全文のような長文をそのままログへ通す」ことを構造的に禁止している。
/// <see cref="MaskPersonalInfo"/>は<see cref="MaxMaskedTextLength"/>を超える入力を
/// 正規表現マスクの結果ではなく完全な代替文字列（<c>[REDACTED: ...]</c>）に置き換えるため、
/// 呼び出し元が誤ってOCR全文（通常は数百〜数千文字）を渡しても、原文が一切ログへ出力されない。
/// 短い自由記述（ファイル名から推定した会社名・タイトル等）向けの正規表現マスクは、
/// あくまで「最低限」の住所・氏名らしきパターン検出であり、完全な個人情報検出を保証しない。
/// </remarks>
public static class LogMasker
{
    /// <summary>
    /// <see cref="MaskPersonalInfo"/>が正規表現マスクを適用する入力の上限文字数。
    /// これを超える入力（OCR全文等の長文）はマスクせず、完全に代替文字列へ置き換える。
    /// </summary>
    internal const int MaxMaskedTextLength = 200;

    // --- 住所・氏名らしき文字列パターン（正規表現ベースの最低限の検出） -----------------

    /// <summary>郵便番号（例: 123-4567 / 〒123-4567）。</summary>
    private static readonly Regex ZipCodePattern =
        new(@"〒?\d{3}-\d{4}", RegexOptions.Compiled);

    /// <summary>
    /// 都道府県名に続けて市区町村・番地等が続く住所らしき文字列
    /// （例: 東京都渋谷区代々木2-3-1、大阪府大阪市北区梅田1丁目）。
    /// </summary>
    private static readonly Regex AddressPattern = new(
        @"(北海道|東京都|(?:京都|大阪)府|(?:青森|岩手|宮城|秋田|山形|福島|茨城|栃木|群馬|埼玉|千葉|神奈川|新潟|富山|石川|福井|山梨|長野|岐阜|静岡|愛知|三重|滋賀|兵庫|奈良|和歌山|鳥取|島根|岡山|広島|山口|徳島|香川|愛媛|高知|福岡|佐賀|長崎|熊本|大分|宮崎|鹿児島|沖縄)県)[^\s、。]{0,30}[市区町村郡][^\s、。]{0,30}",
        RegexOptions.Compiled);

    /// <summary>
    /// 電話番号（固定電話・携帯電話）。例: 03-1234-5678 / 090-1234-5678。
    /// </summary>
    private static readonly Regex PhoneNumberPattern =
        new(@"0\d{1,4}-\d{1,4}-\d{4}", RegexOptions.Compiled);

    /// <summary>メールアドレス。</summary>
    private static readonly Regex EmailPattern =
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// 敬称付きの氏名らしき文字列（例: 山田太郎様、鈴木花子さん、田中殿）。
    /// 姓名を確実に判定することはできないため、直前の1〜10文字＋敬称という
    /// 「らしさ」でヒットさせる最低限の検出にとどめる。
    /// </summary>
    private static readonly Regex HonorificNamePattern = new(
        @"[一-龠々ぁ-んァ-ヶー]{1,10}(様|さん|殿)",
        RegexOptions.Compiled);

    /// <summary>
    /// ラベル付きの氏名・住所（例: 氏名: 山田太郎、お名前：鈴木花子、住所: 東京都〜）。
    /// OCR/フォーム抽出結果でよく見る「ラベル: 値」形式を最低限カバーする。
    /// </summary>
    private static readonly Regex LabeledPersonalInfoPattern = new(
        @"(氏名|お名前|名前|住所|担当者)\s*[:：]\s*[^\s、。]+",
        RegexOptions.Compiled);

    /// <summary>
    /// ファイルパス（絶対パス・相対パスいずれも可）をSHA256でハッシュ化する。
    /// 元のパスを復元できない一方向ハッシュであり、同一パスからは常に同一の値を返すため、
    /// サポートログ上で「同じファイルに関する行かどうか」の突き合わせには利用できる。
    /// </summary>
    /// <param name="path">ハッシュ化するファイルパス（<see langword="null"/>/空文字なら空文字を返す）。</param>
    /// <returns><c>sha256:</c>接頭辞付きの64桁16進文字列（小文字）。</returns>
    public static string HashPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // OS間の区切り文字違い（\ と /）だけで別ハッシュにならないよう正規化する。
        string normalized = path.Replace('/', '\\').Trim();
        return "sha256:" + ComputeHexSha256(normalized);
    }

    /// <summary>
    /// ファイル名（パスを含まない単体のファイル名）をSHA256でハッシュ化する。
    /// 拡張子は個人情報ではなく診断に有用なため、ハッシュ化対象から除外して末尾に残す
    /// （例: <c>invoice_2026.pdf</c> → <c>sha256:xxxxxxxx....pdf</c>）。
    /// </summary>
    /// <param name="fileName">ハッシュ化するファイル名（<see langword="null"/>/空文字なら空文字を返す）。</param>
    public static string HashFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        string extension = System.IO.Path.GetExtension(fileName);
        string nameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(fileName);

        string hash = "sha256:" + ComputeHexSha256(nameWithoutExtension);
        return hash + extension;
    }

    /// <summary>
    /// 自由記述テキストから住所・氏名らしき文字列を検出し、<c>[MASKED]</c>に置換する
    /// （正規表現ベースの最低限の実装。仕様書§7.2-7準拠）。
    /// </summary>
    /// <remarks>
    /// <paramref name="text"/>が<see cref="MaxMaskedTextLength"/>文字を超える場合は、
    /// 正規表現マスクを適用せず<c>[REDACTED: text length=N exceeds log-safe limit]</c>に
    /// 完全に置き換える。これはOCR全文のような長文がマスク漏れ経由でログに残留することを
    /// 防ぐための構造的なガードであり、短い自由記述（会社名・件名等）のみを対象とする
    /// 想定の関数であることを表す。
    /// </remarks>
    /// <param name="text">マスク対象のテキスト（<see langword="null"/>/空文字なら空文字を返す）。</param>
    public static string MaskPersonalInfo(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length > MaxMaskedTextLength)
        {
            return $"[REDACTED: text length={text.Length} exceeds log-safe limit]";
        }

        string masked = text;
        masked = LabeledPersonalInfoPattern.Replace(masked, "[MASKED]");
        masked = AddressPattern.Replace(masked, "[MASKED]");
        masked = ZipCodePattern.Replace(masked, "[MASKED]");
        masked = PhoneNumberPattern.Replace(masked, "[MASKED]");
        masked = EmailPattern.Replace(masked, "[MASKED]");
        masked = HonorificNamePattern.Replace(masked, "[MASKED]");
        return masked;
    }

    private static string ComputeHexSha256(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }
}
