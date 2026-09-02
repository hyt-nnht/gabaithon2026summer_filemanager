using FileOrganizer.Core.Database;
using FileOrganizer.Shared.Models;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Tests.Database;

/// <summary>
/// 仕様書§7.2-6「プライバシー」（OCR抽出テキスト全文はDBやログに永続化せず、リネーム変数生成後に
/// メモリから即座に破棄すること）の検証。
/// </summary>
/// <remarks>
/// <see cref="FileOrganizer.Infrastructure.Ocr.WindowsMediaOcrService"/>のXMLドキュメント（プライバシー設計）
/// が示すとおり、OCR全文は<see cref="FileMetadata.OcrText"/>（プロセスメモリ上のみに存在する一時モデル）
/// を経由するだけで、DB永続化モデルである<see cref="HistoryRecord"/>には最初からOCR文字列を保持する
/// プロパティが存在しない。つまり「うっかり書いてしまう」経路自体が型システム上存在しない設計になっている。
/// 本クラスはこれをリフレクション（型定義そのもの）とDB実スキーマ（<c>PRAGMA table_info</c>）の両面から
/// 直接検証し、将来<see cref="HistoryRecord"/>やDDLにOCRテキスト保持用カラムがうっかり追加された場合に
/// 検知できる回帰ガードとする。
/// </remarks>
public class OcrPrivacyTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "OcrPrivacy", Guid.NewGuid().ToString("N"));

    public OcrPrivacyTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの後始末失敗は無視。
        }
    }

    [Fact]
    public void HistoryRecord_OCR全文を保持するプロパティを持たない()
    {
        // DB永続化モデル（SqliteHistoryRepositoryが読み書きする型）にOCRテキスト用フィールドが
        // 存在しないことを型定義そのものから確認する。存在しなければ、実装ミスでOCR全文を
        // うっかりDBへ書き込んでしまうこと自体が構造的に不可能になる。
        var suspiciousProperties = typeof(HistoryRecord).GetProperties()
            .Where(p => p.Name.Contains("Ocr", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("ExtractedText", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(suspiciousProperties);
    }

    [Fact]
    public async Task operation_historyテーブルにOCR全文を保持するカラムが存在しない()
    {
        string connectionString = DatabaseInitializer.BuildConnectionString(Path.Combine(_workDir, "history.db"));
        await new DatabaseInitializer(connectionString).InitializeAsync();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(operation_history);";

        var columnNames = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columnNames.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        Assert.NotEmpty(columnNames); // 前提: テーブル自体は存在する。
        Assert.DoesNotContain(columnNames, c =>
            c.Contains("ocr", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("extracted_text", StringComparison.OrdinalIgnoreCase));
    }
}
