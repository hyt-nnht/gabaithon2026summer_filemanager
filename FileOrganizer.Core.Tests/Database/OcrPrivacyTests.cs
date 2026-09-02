using System.Reflection;
using FileOrganizer.Core.Database;
using FileOrganizer.Shared.Models;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Tests.Database;

/// <summary>
/// 仕様書§7.2-6「プライバシー: OCR抽出テキスト全文をDB・ログに永続化しないこと」
/// （CLAUDE.mdの「プライバシー」ルールにも対応）の受け入れ基準を検証する。
/// 対象: <see cref="HistoryRecord"/>（<c>SqliteHistoryRepository</c>が読み書きする型）と、
/// 実際に<see cref="DatabaseInitializer"/>で作成される<c>operation_history</c>テーブルの実スキーマ。
/// </summary>
/// <remarks>
/// コード検査（「OCR全文はDBへ渡らない設計になっている」という静的な確認）だけでは、
/// 将来の実装変更で回帰しても検知できない。そこで本クラスは、
/// 1) DB永続化モデルの型定義（リフレクション）と
/// 2) 実際に初期化したSQLite DBの実スキーマ（<c>PRAGMA table_info</c>）
/// の両方を実行時に直接検証し、「OCR全文を保持する経路がそもそも存在しないこと」を
/// 実行するたびに保証する回帰ガードとして機能する。
/// </remarks>
public class OcrPrivacyTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "OcrPrivacy", Guid.NewGuid().ToString("N"));

    public OcrPrivacyTests()
    {
        Directory.CreateDirectory(_workDir);
    }

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

    /// <summary>
    /// OCR全文を保持しうる名前（<c>Ocr</c>/<c>ExtractedText</c>を含む）かどうかを、
    /// 大文字小文字を問わず判定する。
    /// </summary>
    private static bool LooksLikeOcrFullTextPropertyName(string propertyName)
        => propertyName.Contains("Ocr", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("ExtractedText", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 実DBのカラム名（スネークケース）についても同様に判定する。
    /// </summary>
    private static bool LooksLikeOcrFullTextColumnName(string columnName)
        => columnName.Contains("ocr", StringComparison.OrdinalIgnoreCase)
        || columnName.Contains("extracted_text", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void HistoryRecord_OcrまたはExtractedTextを含む名前のプロパティが存在しない()
    {
        PropertyInfo[] properties = typeof(HistoryRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        string[] offendingProperties = properties
            .Select(p => p.Name)
            .Where(LooksLikeOcrFullTextPropertyName)
            .ToArray();

        Assert.True(
            offendingProperties.Length == 0,
            "HistoryRecordにOCR全文を保持しうるプロパティが見つかりました: " + string.Join(", ", offendingProperties) +
            "。CLAUDE.mdの「プライバシー」ルール（OCR抽出テキスト全文をDB・ログに永続化しない）に違反する可能性があります。");
    }

    [Fact]
    public async Task operation_historyテーブルの実スキーマにOcrまたはextracted_textを含むカラムが存在しない()
    {
        string dbPath = Path.Combine(_workDir, "organizer.db");
        string connectionString = DatabaseInitializer.BuildConnectionString(dbPath);
        var initializer = new DatabaseInitializer(connectionString);

        await initializer.InitializeAsync();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(operation_history);";

        var columnNames = new List<string>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            // PRAGMA table_info の列: cid, name, type, notnull, dflt_value, pk
            int nameOrdinal = reader.GetOrdinal("name");
            while (await reader.ReadAsync())
            {
                columnNames.Add(reader.GetString(nameOrdinal));
            }
        }

        Assert.NotEmpty(columnNames); // テーブル自体が存在し、カラムを取得できていることの前提確認。

        string[] offendingColumns = columnNames.Where(LooksLikeOcrFullTextColumnName).ToArray();

        Assert.True(
            offendingColumns.Length == 0,
            "operation_historyテーブルにOCR全文を保持しうるカラムが見つかりました: " + string.Join(", ", offendingColumns) +
            "。CLAUDE.mdの「プライバシー」ルール（OCR抽出テキスト全文をDB・ログに永続化しない）に違反する可能性があります。");
    }
}
