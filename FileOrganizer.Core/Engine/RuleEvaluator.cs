using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>
/// <see cref="IRuleEngine"/>の実装。AI_IMPLEMENTATION_GUIDE.md §1.1の
/// <see cref="RuleModel"/>/<see cref="RuleCondition"/>/<see cref="RuleAction"/>を入力に、
/// 対象ファイルのメタデータへ条件を評価する。
/// </summary>
/// <remarks>
/// <para>
/// <b>評価順序（仕様書§6）</b>: <paramref name="rules"/>はリスト上位ほど優先度が高いものとして扱う。
/// <c>applyAllMatchingRules=false</c>（<c>AppSettings.ApplyAllMatchingRules</c>の既定値）の場合は、
/// リストを先頭から評価し、最初に一致した1件のみを<see cref="RuleEvaluationResult.MatchedRule"/>として
/// 返した時点で評価を打ち切る。<c>true</c>の場合は全ルールを評価し、一致した全件を
/// <see cref="RuleEvaluationResult.AllMatchedRules"/>に格納する（先頭が最優先ルールである点は変わらない）。
/// </para>
/// <para>
/// <b>ルール内の複数条件</b>: 1ルールが複数<see cref="RuleCondition"/>を持つ場合はAND評価（全条件を
/// 満たした場合のみそのルールが一致）とする。条件が0件のルールは、誤設定による無条件マッチ
/// （＝実質すべてのファイルに適用されてしまう事故）を避けるため、常に不一致として扱う。
/// </para>
/// <para>
/// <b>対応する条件種別（<c>RuleCondition.Type</c>）</b>: <c>extension</c>, <c>filename</c>,
/// <c>size_mb</c>, <c>days_old</c> を評価する。<c>ai_category</c>, <c>ocr_contains</c>は
/// Phase2（Python/OCR連携）で<see cref="FileMetadata.AiCategory"/>/<see cref="FileMetadata.OcrText"/>が
/// 実際に埋まるまでの間、評価パイプラインだけを用意しておく（未設定時は常に不一致）。
/// </para>
/// </remarks>
public class RuleEvaluator : IRuleEngine
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public RuleEvaluationResult Evaluate(FileMetadata metadata, IReadOnlyList<RuleModel> rules, bool applyAllMatchingRules)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rules);

        var result = new RuleEvaluationResult();

        foreach (var rule in rules) // リスト上位 = 優先ルール（仕様書§6「評価順序」）
        {
            if (!rule.Enabled) continue;
            if (!MatchesRule(rule, metadata)) continue;

            result.AllMatchedRules.Add(rule);

            if (!applyAllMatchingRules)
            {
                // 既定: リスト上位の優先ルール1件のみ実行。以降の評価は打ち切る。
                result.IsMatched = true;
                result.MatchedRule = rule;
                return result;
            }
        }

        if (applyAllMatchingRules && result.AllMatchedRules.Count > 0)
        {
            result.IsMatched = true;
            result.MatchedRule = result.AllMatchedRules[0]; // 代表として最優先の一致ルールを設定
        }

        return result;
    }

    private static bool MatchesRule(RuleModel rule, FileMetadata metadata)
    {
        if (rule.Conditions.Count == 0) return false;

        foreach (var condition in rule.Conditions)
        {
            if (!MatchesCondition(condition, metadata)) return false; // AND評価
        }
        return true;
    }

    private static bool MatchesCondition(RuleCondition condition, FileMetadata metadata) => condition.Type switch
    {
        "extension" => EvaluateExtension(condition, metadata),
        "filename" => EvaluateFilename(condition, metadata),
        "size_mb" => EvaluateNumeric(condition, metadata.SizeBytes / (1024.0 * 1024.0)),
        "days_old" => EvaluateNumeric(condition, metadata.DaysOld),
        "ocr_contains" => EvaluateOcrContains(condition, metadata),
        "ai_category" => EvaluateAiCategory(condition, metadata),
        _ => false, // 未知の条件種別は安全側（不一致）に倒す
    };

    // --- extension --------------------------------------------------------------------

    private static bool EvaluateExtension(RuleCondition condition, FileMetadata metadata)
    {
        string actual = NormalizeExtension(metadata.Extension);
        return condition.Operator switch
        {
            "equals" => string.Equals(actual, NormalizeExtension(ToSingleString(condition.Value)), StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(NormalizeExtension(ToSingleString(condition.Value)), StringComparison.OrdinalIgnoreCase),
            "regex" => MatchesRegex(actual, ToSingleString(condition.Value)),
            "in" => ToStringList(condition.Value).Select(NormalizeExtension).Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase)),
            _ => false, // greater_than/less_than は拡張子には意味を持たない
        };
    }

    private static string NormalizeExtension(string? ext)
        => string.IsNullOrEmpty(ext) ? string.Empty : ext.TrimStart('.').ToLowerInvariant();

    // --- filename -----------------------------------------------------------------------

    private static bool EvaluateFilename(RuleCondition condition, FileMetadata metadata)
    {
        string actual = metadata.FileName ?? string.Empty;
        return condition.Operator switch
        {
            "equals" => string.Equals(actual, ToSingleString(condition.Value), StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(ToSingleString(condition.Value) ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "regex" => MatchesRegex(actual, ToSingleString(condition.Value)),
            "in" => ToStringList(condition.Value).Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    // --- size_mb / days_old（数値条件は共通ロジック） -------------------------------------

    private const double NumericEpsilon = 1e-6;

    private static bool EvaluateNumeric(RuleCondition condition, double actual) => condition.Operator switch
    {
        "equals" => ToDouble(condition.Value) is double eq && Math.Abs(actual - eq) < NumericEpsilon,
        "greater_than" => ToDouble(condition.Value) is double gt && actual > gt,
        "less_than" => ToDouble(condition.Value) is double lt && actual < lt,
        "in" => ToStringList(condition.Value)
            .Select(TryParseDouble)
            .Any(d => d.HasValue && Math.Abs(actual - d.Value) < NumericEpsilon),
        _ => false, // contains/regexは数値条件には意味を持たない
    };

    // --- ai_category / ocr_contains（Phase2で本格利用。現時点は評価経路のみ用意） -------------

    private static bool EvaluateOcrContains(RuleCondition condition, FileMetadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.OcrText)) return false; // Phase2でPython解析結果が入るまでは常に不一致
        return condition.Operator switch
        {
            "equals" => string.Equals(metadata.OcrText, ToSingleString(condition.Value), StringComparison.OrdinalIgnoreCase),
            "contains" => metadata.OcrText.Contains(ToSingleString(condition.Value) ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "regex" => MatchesRegex(metadata.OcrText, ToSingleString(condition.Value)),
            _ => false,
        };
    }

    private static bool EvaluateAiCategory(RuleCondition condition, FileMetadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.AiCategory)) return false; // Phase2でAI分類結果が入るまでは常に不一致
        return condition.Operator switch
        {
            "equals" => string.Equals(metadata.AiCategory, ToSingleString(condition.Value), StringComparison.OrdinalIgnoreCase),
            "contains" => metadata.AiCategory.Contains(ToSingleString(condition.Value) ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "regex" => MatchesRegex(metadata.AiCategory, ToSingleString(condition.Value)),
            "in" => ToStringList(condition.Value).Any(v => string.Equals(v, metadata.AiCategory, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    // --- regex共通処理（コンパイル結果をキャッシュ） -----------------------------------------

    private static bool MatchesRegex(string actual, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try
        {
            var regex = RegexCache.GetOrAdd(pattern, static p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            return regex.IsMatch(actual);
        }
        catch (ArgumentException)
        {
            // 不正な正規表現パターン（ユーザー入力起因等）→ 例外で評価全体を止めず不一致として扱う。
            return false;
        }
    }

    // --- RuleCondition.Value の型正規化ヘルパー ----------------------------------------------
    // RuleModelはJSON設定ファイルからSystem.Text.Jsonでデシリアライズされる想定のため、
    // object?型のValueはCLR型（string/数値/配列）とJsonElementの両方があり得る。

    private static string? ToSingleString(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => je.GetBoolean().ToString(),
                _ => je.ToString(),
            };
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> ToStringList(object? value)
    {
        switch (value)
        {
            case null:
                return Array.Empty<string>();
            case string s:
                return new[] { s }; // 単一文字列も1要素リストとして扱う
            case JsonElement { ValueKind: JsonValueKind.Array } je:
                return je.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())
                    .ToList();
            case IEnumerable enumerable:
                return enumerable.Cast<object?>().Select(o => ToSingleString(o) ?? string.Empty).ToList();
            default:
                return new[] { ToSingleString(value) ?? string.Empty };
        }
    }

    private static double? ToDouble(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case double d:
                return d;
            case float f:
                return f;
            case int i:
                return i;
            case long l:
                return l;
            case decimal dec:
                return (double)dec;
            case JsonElement { ValueKind: JsonValueKind.Number } je:
                return je.GetDouble();
            case string s:
                return TryParseDouble(s);
            default:
                return null;
        }
    }

    private static double? TryParseDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
