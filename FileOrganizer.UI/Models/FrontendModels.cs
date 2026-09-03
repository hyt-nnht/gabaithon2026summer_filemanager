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
    string AiStatus,
    bool IsAiAnalyzing = false);

public sealed record FrontendActionResult(bool Success, string Message)
{
    public static FrontendActionResult Completed(string message) => new(true, message);
    public static FrontendActionResult Deferred(string message) => new(false, message);
}

public sealed class DryRunPreviewItem
{
    public string SourcePath { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public IReadOnlyList<DryRunPreviewAction> Actions { get; init; } = Array.Empty<DryRunPreviewAction>();
    public long SourceSizeBytes { get; init; }
    public DateTime SourceLastWriteTimeUtc { get; init; }
    public string SourceLightweightHash { get; init; } = string.Empty;
    /// <summary>プレビュー内容を実行直前に照合するための署名。ファイル内容そのものは含めない。</summary>
    public string PlanSignature { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string? ClassificationSource { get; init; }
    public string? ClassificationCategory { get; init; }

    public bool RequiresConfirmation => Actions.Any(action => action.RequiresConfirmation);
    public string? DestinationPath => Actions.LastOrDefault(action => action.DestinationPath is not null)?.DestinationPath;
}

public sealed class DryRunPreviewAction
{
    public OperationType OperationType { get; init; }
    public string? DestinationPath { get; init; }
    public bool WillSkip { get; init; }
    public bool RequiresConfirmation { get; init; }
}

public sealed class BackendActivityEventArgs(string? message = null) : EventArgs
{
    public string? Message { get; } = message;
}
