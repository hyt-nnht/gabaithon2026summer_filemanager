using FileOrganizer.Shared.Models;

namespace FileOrganizer.UI.Models;

public sealed record FrontendSnapshot(
    AppSettings Settings,
    IReadOnlyList<RuleModel> Rules,
    IReadOnlyList<HistoryRecord> RecentHistory,
    MonitoringSnapshot Monitoring);

public sealed record MonitoringSnapshot(
    bool IsMonitoring,
    int PendingFiles,
    int OrganizedToday,
    DateTimeOffset? LastProcessedAt,
    string AiStatus);

public sealed record FrontendActionResult(bool Success, string Message)
{
    public static FrontendActionResult Completed(string message) => new(true, message);
    public static FrontendActionResult Deferred(string message) => new(false, message);
}

public sealed class DryRunPreviewItem
{
    public string SourcePath { get; init; } = string.Empty;
    public string? DestinationPath { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public OperationType OperationType { get; init; }
    public bool RequiresConfirmation { get; init; }
    public string Note { get; init; } = string.Empty;
}
