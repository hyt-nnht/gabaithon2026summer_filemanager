using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface IRuleEngine
{
    RuleEvaluationResult Evaluate(FileMetadata metadata, IReadOnlyList<RuleModel> rules, bool applyAllMatchingRules);
}
