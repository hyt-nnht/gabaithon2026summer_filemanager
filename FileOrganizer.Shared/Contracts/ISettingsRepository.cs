using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface ISettingsRepository
{
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task<List<RuleModel>> LoadRulesAsync(CancellationToken ct = default);
    Task SaveRulesAsync(List<RuleModel> rules, CancellationToken ct = default);
    Task RestorePresetRulesAsync(CancellationToken ct = default);
}
