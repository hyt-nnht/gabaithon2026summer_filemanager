using System.Linq;
using System.Text.Json;
using FileOrganizer.Core.Engine;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Engine;

/// <summary>
/// 仕様書§6「評価順序」（複数ルール一致時は上位優先1件のみ、または全適用）と、
/// AI_IMPLEMENTATION_GUIDE.md §1.1のcondition type/operator一覧の受け入れ基準を検証する。
/// 対象: <see cref="RuleEvaluator"/>。
/// </summary>
public class RuleEvaluatorTests
{
    private static FileMetadata CreateMetadata(
        string fileName = "report.pdf",
        long sizeBytes = 5 * 1024 * 1024,
        DateTime? lastWriteTimeUtc = null,
        string? ocrText = null,
        string? aiCategory = null)
    {
        return new FileMetadata
        {
            FullPath = $@"C:\watch\{fileName}",
            FileName = fileName,
            Extension = Path.GetExtension(fileName),
            SizeBytes = sizeBytes,
            LastWriteTimeUtc = lastWriteTimeUtc ?? DateTime.UtcNow,
            CreatedTimeUtc = DateTime.UtcNow,
            OcrText = ocrText,
            AiCategory = aiCategory,
        };
    }

    private static RuleModel CreateRule(
        string name,
        IEnumerable<RuleCondition> conditions,
        bool enabled = true)
    {
        return new RuleModel
        {
            Name = name,
            Enabled = enabled,
            Conditions = conditions.ToList(),
            Actions = new List<RuleAction> { new() { Type = "move", Destination = @"D:\organized" } },
        };
    }

    private static RuleCondition Cond(string type, string op, object? value)
        => new() { Type = type, Operator = op, Value = value };

    private readonly RuleEvaluator _evaluator = new();

    // --- extension ------------------------------------------------------------------

    [Theory]
    [InlineData("equals", "pdf", "report.pdf", true)]
    [InlineData("equals", ".pdf", "report.pdf", true)] // 先頭ドット付き値も許容
    [InlineData("equals", "docx", "report.pdf", false)]
    [InlineData("contains", "df", "report.pdf", true)]
    [InlineData("regex", "^pdf$", "report.pdf", true)]
    [InlineData("regex", "^doc", "report.pdf", false)]
    public void Extension条件_各operatorで判定できる(string op, object value, string fileName, bool expected)
    {
        var metadata = CreateMetadata(fileName);
        var rule = CreateRule("r1", new[] { Cond("extension", op, value) });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.Equal(expected, result.IsMatched);
    }

    [Fact]
    public void Extension条件_inでリスト内のいずれかに一致すればtrue()
    {
        var metadata = CreateMetadata("report.docx");
        var rule = CreateRule("r1", new[] { Cond("extension", "in", new[] { "pdf", "docx", "xlsx" }) });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.True(result.IsMatched);
    }

    [Fact]
    public void Extension条件_greater_thanは意味を持たず常に不一致()
    {
        var metadata = CreateMetadata("report.pdf");
        var rule = CreateRule("r1", new[] { Cond("extension", "greater_than", "1") });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.False(result.IsMatched);
    }

    // --- filename ---------------------------------------------------------------------

    [Theory]
    [InlineData("equals", "REPORT.PDF", "report.pdf", true)] // 大文字小文字を区別しない
    [InlineData("equals", "invoice.pdf", "report.pdf", false)]
    [InlineData("contains", "epor", "report.pdf", true)]
    [InlineData("regex", @"^report\.\w+$", "report.pdf", true)]
    public void Filename条件_各operatorで判定できる(string op, object value, string fileName, bool expected)
    {
        var metadata = CreateMetadata(fileName);
        var rule = CreateRule("r1", new[] { Cond("filename", op, value) });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.Equal(expected, result.IsMatched);
    }

    [Fact]
    public void Filename条件_inでリスト内のいずれかに一致すればtrue()
    {
        var metadata = CreateMetadata("invoice.pdf");
        var rule = CreateRule("r1", new[] { Cond("filename", "in", new List<string> { "report.pdf", "invoice.pdf" }) });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.True(result.IsMatched);
    }

    [Fact]
    public void Filename条件_less_thanは意味を持たず常に不一致()
    {
        var metadata = CreateMetadata("report.pdf");
        var rule = CreateRule("r1", new[] { Cond("filename", "less_than", "report.pdf") });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.False(result.IsMatched);
    }

    // --- size_mb ------------------------------------------------------------------------

    [Fact]
    public void SizeMb条件_equalsは誤差許容で一致する()
    {
        var metadata = CreateMetadata(sizeBytes: 5 * 1024 * 1024); // ちょうど5MB
        var rule = CreateRule("r1", new[] { Cond("size_mb", "equals", 5) });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void SizeMb条件_greater_thanで大きいファイルに一致する()
    {
        var metadata = CreateMetadata(sizeBytes: 10 * 1024 * 1024); // 10MB
        var rule = CreateRule("r1", new[] { Cond("size_mb", "greater_than", 5) });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void SizeMb条件_greater_thanは閾値以下なら不一致()
    {
        var metadata = CreateMetadata(sizeBytes: 5 * 1024 * 1024); // ちょうど5MB（厳密不等号）
        var rule = CreateRule("r1", new[] { Cond("size_mb", "greater_than", 5) });

        Assert.False(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void SizeMb条件_less_thanで小さいファイルに一致する()
    {
        var metadata = CreateMetadata(sizeBytes: 1 * 1024 * 1024); // 1MB
        var rule = CreateRule("r1", new[] { Cond("size_mb", "less_than", 5) });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void SizeMb条件_inでリスト内の数値に一致すればtrue()
    {
        var metadata = CreateMetadata(sizeBytes: 3 * 1024 * 1024); // 3MB
        var rule = CreateRule("r1", new[] { Cond("size_mb", "in", new object[] { 1, 2, 3 }) });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void SizeMb条件_containsは意味を持たず常に不一致()
    {
        var metadata = CreateMetadata(sizeBytes: 3 * 1024 * 1024);
        var rule = CreateRule("r1", new[] { Cond("size_mb", "contains", "3") });

        Assert.False(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    // --- days_old -----------------------------------------------------------------------

    [Fact]
    public void DaysOld条件_greater_thanで古いファイルに一致する()
    {
        var metadata = CreateMetadata(lastWriteTimeUtc: DateTime.UtcNow.AddDays(-40));
        var rule = CreateRule("r1", new[] { Cond("days_old", "greater_than", 30) });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void DaysOld条件_less_thanで新しいファイルに一致する()
    {
        var metadata = CreateMetadata(lastWriteTimeUtc: DateTime.UtcNow.AddDays(-1));
        var rule = CreateRule("r1", new[] { Cond("days_old", "less_than", 7) });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void DaysOld条件_greater_thanで新しいファイルには不一致()
    {
        var metadata = CreateMetadata(lastWriteTimeUtc: DateTime.UtcNow.AddDays(-1));
        var rule = CreateRule("r1", new[] { Cond("days_old", "greater_than", 30) });

        Assert.False(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void DaysOld条件_regexは意味を持たず常に不一致()
    {
        var metadata = CreateMetadata(lastWriteTimeUtc: DateTime.UtcNow.AddDays(-40));
        var rule = CreateRule("r1", new[] { Cond("days_old", "regex", "^40$") });

        Assert.False(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    // --- ルール優先順位・ApplyAllMatchingRules（仕様書§6「評価順序」） -----------------------

    [Fact]
    public void 複数ルール一致時_ApplyAllMatchingRulesがfalseならリスト上位1件のみ一致する()
    {
        var metadata = CreateMetadata("report.pdf");
        var ruleA = CreateRule("A: pdf全般", new[] { Cond("extension", "equals", "pdf") });
        var ruleB = CreateRule("B: reportで始まる", new[] { Cond("filename", "contains", "report") });

        var result = _evaluator.Evaluate(metadata, new[] { ruleA, ruleB }, applyAllMatchingRules: false);

        Assert.True(result.IsMatched);
        Assert.Same(ruleA, result.MatchedRule);
        Assert.Single(result.AllMatchedRules);
        Assert.Same(ruleA, result.AllMatchedRules[0]);
    }

    [Fact]
    public void 複数ルール一致時_リストの並び順を入れ替えると優先されるルールも変わる()
    {
        var metadata = CreateMetadata("report.pdf");
        var ruleA = CreateRule("A: pdf全般", new[] { Cond("extension", "equals", "pdf") });
        var ruleB = CreateRule("B: reportで始まる", new[] { Cond("filename", "contains", "report") });

        // 今度はBを先頭にする → Bが優先される
        var result = _evaluator.Evaluate(metadata, new[] { ruleB, ruleA }, applyAllMatchingRules: false);

        Assert.Same(ruleB, result.MatchedRule);
    }

    [Fact]
    public void 複数ルール一致時_ApplyAllMatchingRulesがtrueなら全て一致する()
    {
        var metadata = CreateMetadata("report.pdf");
        var ruleA = CreateRule("A: pdf全般", new[] { Cond("extension", "equals", "pdf") });
        var ruleB = CreateRule("B: reportで始まる", new[] { Cond("filename", "contains", "report") });
        var ruleC = CreateRule("C: 一致しない", new[] { Cond("extension", "equals", "docx") });

        var result = _evaluator.Evaluate(metadata, new[] { ruleA, ruleB, ruleC }, applyAllMatchingRules: true);

        Assert.True(result.IsMatched);
        Assert.Equal(2, result.AllMatchedRules.Count);
        Assert.Same(ruleA, result.AllMatchedRules[0]);
        Assert.Same(ruleB, result.AllMatchedRules[1]);
        Assert.Same(ruleA, result.MatchedRule); // 代表として最優先の一致ルール
    }

    [Fact]
    public void 一致するルールがない場合はIsMatchedがfalseでMatchedRuleもnullになる()
    {
        var metadata = CreateMetadata("report.pdf");
        var rule = CreateRule("r1", new[] { Cond("extension", "equals", "docx") });

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.False(result.IsMatched);
        Assert.Null(result.MatchedRule);
        Assert.Empty(result.AllMatchedRules);
    }

    [Fact]
    public void Enabledがfalseのルールは条件を満たしていても一致しない()
    {
        var metadata = CreateMetadata("report.pdf");
        var rule = CreateRule("r1", new[] { Cond("extension", "equals", "pdf") }, enabled: false);

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.False(result.IsMatched);
    }

    [Fact]
    public void ルール内の複数条件はAND評価される()
    {
        var metadata = CreateMetadata("report.pdf", sizeBytes: 10 * 1024 * 1024);
        var rule = CreateRule("r1", new[]
        {
            Cond("extension", "equals", "pdf"),
            Cond("size_mb", "greater_than", 5),
        });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);

        // 片方の条件だけ満たすファイルは不一致（AND評価の確認）。
        var smallPdf = CreateMetadata("report.pdf", sizeBytes: 1 * 1024 * 1024);
        Assert.False(_evaluator.Evaluate(smallPdf, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void 条件が0件のルールは常に不一致になる()
    {
        var metadata = CreateMetadata("report.pdf");
        var rule = CreateRule("r1", Array.Empty<RuleCondition>());

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.False(result.IsMatched);
    }

    // --- ai_category / ocr_contains（Phase2向けスタブ） -------------------------------------

    [Fact]
    public void OcrContains条件_OcrTextが未設定なら常に不一致()
    {
        var metadata = CreateMetadata(ocrText: null);
        var rule = CreateRule("r1", new[] { Cond("ocr_contains", "contains", "請求書") });

        Assert.False(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void OcrContains条件_OcrText設定済みならcontainsで一致する()
    {
        var metadata = CreateMetadata(ocrText: "株式会社サンプル御中 請求書 2026年8月25日");
        var rule = CreateRule("r1", new[] { Cond("ocr_contains", "contains", "請求書") });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void AiCategory条件_AiCategoryが未設定なら常に不一致()
    {
        var metadata = CreateMetadata(aiCategory: null);
        var rule = CreateRule("r1", new[] { Cond("ai_category", "equals", "請求書") });

        Assert.False(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    [Fact]
    public void AiCategory条件_AiCategory設定済みならequalsで一致する()
    {
        var metadata = CreateMetadata(aiCategory: "請求書");
        var rule = CreateRule("r1", new[] { Cond("ai_category", "equals", "請求書") });

        Assert.True(_evaluator.Evaluate(metadata, new[] { rule }, false).IsMatched);
    }

    // --- JSON往復（System.Text.Jsonデシリアライズ経由のValue型） -----------------------------

    [Fact]
    public void JSONからデシリアライズしたRuleModelでも評価できる()
    {
        const string json = """
        {
          "id": "rule-1",
          "name": "PDF & 5MB超",
          "enabled": true,
          "watch_folder": "C:\\watch",
          "conditions": [
            { "type": "extension", "operator": "in", "value": ["pdf", "docx"] },
            { "type": "size_mb", "operator": "greater_than", "value": 5 }
          ],
          "actions": [ { "type": "move", "destination": "D:\\organized" } ]
        }
        """;
        var rule = JsonSerializer.Deserialize<RuleModel>(json)!;
        var metadata = CreateMetadata("report.pdf", sizeBytes: 10 * 1024 * 1024);

        var result = _evaluator.Evaluate(metadata, new[] { rule }, applyAllMatchingRules: false);

        Assert.True(result.IsMatched);
    }

    // --- 引数検証 -------------------------------------------------------------------------

    [Fact]
    public void Evaluate_metadataがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Evaluate(null!, Array.Empty<RuleModel>(), false));
    }

    [Fact]
    public void Evaluate_rulesがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _evaluator.Evaluate(CreateMetadata(), null!, false));
    }

    [Fact]
    public void Evaluate_ルールが空リストの場合は不一致になる()
    {
        var result = _evaluator.Evaluate(CreateMetadata(), Array.Empty<RuleModel>(), false);
        Assert.False(result.IsMatched);
    }
}
