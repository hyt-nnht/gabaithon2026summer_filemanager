namespace FileOrganizer.Shared.Models;

/// <summary>
/// ルール評価結果。
/// </summary>
public class RuleEvaluationResult
{
    public bool IsMatched { get; set; }
    public RuleModel? MatchedRule { get; set; }
    public List<RuleModel> AllMatchedRules { get; set; } = new(); // ApplyAllMatchingRules=true用
}
