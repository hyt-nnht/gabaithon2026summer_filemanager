using FileOrganizer.Core.Utils;

namespace FileOrganizer.Core.Tests.Utils;

/// <summary>
/// 仕様書§6「ファイル名サニタイズ（末尾ドット問題の解決）」の受け入れ基準を検証する。
/// 対象: <see cref="PathSanitizer.SanitizeFileName"/>（AI_IMPLEMENTATION_GUIDE.md §5.1準拠実装）。
/// </summary>
public class PathSanitizerTests
{
    // --- 禁止文字（\ / : * ? " < > |）を含むファイル名 -----------------------------

    [Theory]
    [InlineData("file:name.txt", "file_name.txt")]
    [InlineData("file*name.txt", "file_name.txt")]
    [InlineData("file?name.txt", "file_name.txt")]
    [InlineData("file\"name.txt", "file_name.txt")]
    [InlineData("file<name.txt", "file_name.txt")]
    [InlineData("file>name.txt", "file_name.txt")]
    [InlineData("file|name.txt", "file_name.txt")]
    public void SanitizeFileName_禁止文字はアンダースコアに置換される(string raw, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(raw));
    }

    [Fact]
    public void SanitizeFileName_複数の禁止文字を含む場合は全て置換される()
    {
        // ? と * の2文字を含むケース。
        string result = PathSanitizer.SanitizeFileName("a?b*c.txt");
        Assert.Equal("a_b_c.txt", result);
    }

    [Fact]
    public void SanitizeFileName_パス区切り文字を含む場合でも安全な単一ファイル名になる()
    {
        // \ と / は Path.GetInvalidFileNameChars() にも含まれる禁止文字だが、
        // Path.GetFileNameWithoutExtension/GetExtension が区切り文字として先に解釈するため、
        // 最終セグメントのみが残る。結果として \ / を含まない安全な名前になることを保証する。
        string result = PathSanitizer.SanitizeFileName("sub\\folder\\report:name.txt");

        Assert.Equal("report_name.txt", result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain('/', result);
    }

    [Fact]
    public void SanitizeFileName_先頭がパス区切り文字のみの場合でも例外を投げない()
    {
        string result = PathSanitizer.SanitizeFileName("\\bad.txt");
        Assert.Equal("bad.txt", result);
    }

    // --- Windows予約デバイス名（大文字小文字混在） -----------------------------------

    [Theory]
    [InlineData("CON", "CON_file")]
    [InlineData("con", "con_file")]
    [InlineData("Con", "Con_file")]
    [InlineData("NUL", "NUL_file")]
    [InlineData("nul", "nul_file")]
    [InlineData("AUX", "AUX_file")]
    [InlineData("PRN", "PRN_file")]
    [InlineData("COM1", "COM1_file")]
    [InlineData("com1", "com1_file")]
    [InlineData("CoM1", "CoM1_file")]
    [InlineData("LPT1", "LPT1_file")]
    public void SanitizeFileName_予約デバイス名は大文字小文字を問わず_fileが付与される(string raw, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(raw));
    }

    [Theory]
    [InlineData("CON.txt", "CON_file.txt")]
    [InlineData("con.txt", "con_file.txt")]
    [InlineData("nul.log", "nul_file.log")]
    [InlineData("com1.txt", "com1_file.txt")]
    public void SanitizeFileName_拡張子付きの予約デバイス名も_fileが付与される(string raw, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(raw));
    }

    [Theory]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("CONFIG.ini", "CONFIG.ini")]
    [InlineData("PRINTER.doc", "PRINTER.doc")]
    public void SanitizeFileName_予約名を接頭辞に含むだけの通常名は変更されない(string raw, string expected)
    {
        // 完全一致判定であることの確認（"CONSOLE" が "CON" と誤判定されないこと）。
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(raw));
    }

    // --- 末尾がドット・空白のファイル名 -----------------------------------------------

    [Theory]
    [InlineData("report.", "report")]
    [InlineData("report .", "report")]
    [InlineData("report  .", "report")]
    [InlineData("report...", "report")]
    public void SanitizeFileName_ファイル名本体の末尾のドットと空白は除去される(string raw, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(raw));
        Assert.False(expected.EndsWith('.') || expected.EndsWith(' '));
    }

    // --- 拡張子側にもドット・空白が付くケース -----------------------------------------

    [Fact]
    public void SanitizeFileName_拡張子の末尾に空白がついていても除去される()
    {
        // "data.csv. " -> 実質的な拡張子は空になり、本体側の "data.csv" がそのまま残る。
        string result = PathSanitizer.SanitizeFileName("data.csv. ");
        Assert.Equal("data.csv", result);
    }

    [Fact]
    public void SanitizeFileName_拡張子の末尾にドットがついていても除去される()
    {
        // "sample.PDF." -> 拡張子側の末尾ドットが除去され、大文字小文字はそのまま保持される。
        string result = PathSanitizer.SanitizeFileName("sample.PDF.");
        Assert.Equal("sample.PDF", result);
    }

    [Fact]
    public void SanitizeFileName_本体と拡張子の間の二重ドットは単一ドットに正規化される()
    {
        // "report..txt" のような二重ドットは、本体側の末尾ドットがトリムされるため
        // 結合時に単一ドットへ正規化される。
        string result = PathSanitizer.SanitizeFileName("report..txt");
        Assert.Equal("report.txt", result);
        Assert.DoesNotContain("..", result);
    }

    // --- 空文字列になってしまうケース -------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("....")]
    public void SanitizeFileName_サニタイズ後に空文字列になる場合はrenamed_fileにフォールバックする(string raw)
    {
        Assert.Equal("renamed_file", PathSanitizer.SanitizeFileName(raw));
    }

    [Fact]
    public void SanitizeFileName_本体が空白のみでも拡張子が有効なら拡張子は保持される()
    {
        // "  .txt" -> 本体は空白のみのため renamed_file にフォールバックしつつ、
        // 有効な拡張子 ".txt" は保持される。
        string result = PathSanitizer.SanitizeFileName("  .txt");
        Assert.Equal("renamed_file.txt", result);
    }

    // --- 通常ケース（回帰防止の対照群） ------------------------------------------------

    [Theory]
    [InlineData("sample.pdf", "sample.pdf")]
    [InlineData("archive.tar.gz", "archive.tar.gz")]
    [InlineData("報告書.docx", "報告書.docx")]
    public void SanitizeFileName_正常なファイル名は変更されない(string raw, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(raw));
    }
}
