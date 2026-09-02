using System.IO;
using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Models;

namespace FileOrganizer.UI.Services;

/// <summary>
/// フロント単体確認用Gateway。実ファイル操作、SQLite、Rules.json保存、Python起動を一切行わない。
/// 画面操作で更新された設定・ルールは、このプロセスのメモリ内だけで保持する。
/// </summary>
public sealed class DesignTimeBackendGateway : IFrontendBackendGateway
{
    private AppSettings _settings = CreateSettings();
    private List<RuleModel> _rules = CreateRules();

    public bool IsBackendConnected => false;
    // デザイン時Gatewayはバックエンドイベントを発生させないため、購読だけを受け入れる。
    // 自動生成フィールドを持たせないことで、未使用イベント警告（CS0067）も避ける。
    public event EventHandler<BackendActivityEventArgs>? ActivityOccurred
    {
        add { }
        remove { }
    }

    public Task<FrontendSnapshot> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = new FrontendSnapshot(
            CloneSettings(_settings),
            CloneRules(_rules),
            CreateHistory(),
            new MonitoringSnapshot(true, 0, 12, DateTimeOffset.Now.AddMinutes(-8), "ローカルAI 待機中"));
        return Task.FromResult(snapshot);
    }

    public Task SaveRulesAsync(IReadOnlyList<RuleModel> rules, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rules = CloneRules(rules);
        return Task.CompletedTask;
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _settings = CloneSettings(settings);
        return Task.CompletedTask;
    }

    public Task<FrontendActionResult> SetMonitoringAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(FrontendActionResult.Deferred(
            enabled
                ? "監視開始UIを確認しました。バックエンド接続後にフォルダ監視を開始します。"
                : "一時停止UIを確認しました。バックエンド接続後に監視を停止します。"));
    }

    public Task<IReadOnlyList<DryRunPreviewItem>> PreviewCleanupAsync(string folderPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // ProductionではDryRunSimulatorの戻り値をこの表示DTOへ投影する。
        IReadOnlyList<DryRunPreviewItem> result = new[]
        {
            new DryRunPreviewItem
            {
                SourcePath = Path.Combine(folderPath, "scan_0042.pdf"),
                RuleName = "請求書を会社別に整理",
                Actions = [new DryRunPreviewAction { OperationType = OperationType.Move, DestinationPath = @"C:\Users\demo\Documents\請求書\2026-08-25_テックサプライ_請求書.pdf" }],
                Note = "OCR → ローカルAIの命名候補"
            },
            new DryRunPreviewItem
            {
                SourcePath = Path.Combine(folderPath, "IMG_3821.jpg"),
                RuleName = "画像を種類別に整理",
                Actions = [new DryRunPreviewAction { OperationType = OperationType.Move, DestinationPath = @"C:\Users\demo\Pictures\Screenshots\IMG_3821.jpg" }],
                Note = "拡張子ルール"
            },
            new DryRunPreviewItem
            {
                SourcePath = Path.Combine(folderPath, "meeting-notes.txt"),
                RuleName = "テキストメモ",
                Actions = [new DryRunPreviewAction { OperationType = OperationType.Copy, DestinationPath = @"C:\Users\demo\Documents\Notes\meeting-notes.txt", RequiresConfirmation = true }],
                Note = "同名ファイルあり（実行前に連番を再確認）"
            }
        };
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DryRunPreviewItem>> PreviewFilesAsync(IReadOnlyList<string> filePaths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string folder = filePaths.Count > 0 ? Path.GetDirectoryName(filePaths[0]) ?? string.Empty : string.Empty;
        return PreviewCleanupAsync(folder, ct);
    }

    public Task<IReadOnlyList<HistoryRecord>> LoadHistoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CreateHistory());
    }

    public Task<FrontendActionResult> ExecuteCleanupAsync(IReadOnlyList<DryRunPreviewItem> approvedItems, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(FrontendActionResult.Deferred(
            $"{approvedItems.Count}件を承認しました。バックエンド接続前のためファイル操作は行っていません。"));
    }

    public Task<UndoResult> UndoAsync(long historyRecordId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new UndoResult
        {
            Outcome = UndoOutcome.Failed,
            Message = "バックエンド接続前のため復元は行っていません。"
        });
    }

    public Task<FrontendActionResult> ExportDiagnosticsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(FrontendActionResult.Deferred(
            "出力先選択UIを確認しました。接続後は個人情報をマスクしてZIPを生成します。"));
    }

    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;

    private static AppSettings CreateSettings() => new()
    {
        WatchFolders = new List<WatchFolderSetting>
        {
            new() { Path = @"C:\Users\demo\Downloads", Enabled = true, IncludeSubdirectories = false },
            new() { Path = @"C:\Users\demo\Desktop\Inbox", Enabled = true, IncludeSubdirectories = true }
        },
        StabilityCheckIntervalMs = 750,
        PeriodicScanIntervalHours = 24,
        ApplyAllMatchingRules = false,
        IsQuickLookEnabled = true,
        QuickLookShortcut = "Space",
        PythonPort = 0,
        UsePreloadedSlmModel = true,
        SlmModelPath = @".\models\gemma-2-2b-it-Q4_K_M.gguf",
        EnableToastNotifications = true,
        WalCheckpointIntervalMinutes = 60
    };

    private static List<RuleModel> CreateRules() => new()
    {
        new RuleModel
        {
            Name = "請求書を会社別に整理",
            Enabled = true,
            WatchFolder = @"C:\Users\demo\Downloads",
            Conditions = new List<RuleCondition>
            {
                new() { Type = "extension", Operator = "in", Value = ".pdf, .png, .jpg" },
                new() { Type = "ai_category", Operator = "equals", Value = "請求書" }
            },
            Actions = new List<RuleAction>
            {
                new() { Type = "rename", Pattern = "{date}_{company}_{document_type}{ext}" },
                new() { Type = "move", Destination = @"C:\Users\demo\Documents\請求書" }
            }
        },
        new RuleModel
        {
            Name = "スクリーンショットを整理",
            Enabled = true,
            WatchFolder = @"C:\Users\demo\Downloads",
            Conditions = new List<RuleCondition>
            {
                new() { Type = "extension", Operator = "in", Value = ".png, .jpg, .jpeg" }
            },
            Actions = new List<RuleAction>
            {
                new() { Type = "move", Destination = @"C:\Users\demo\Pictures\Screenshots" }
            }
        },
        new RuleModel
        {
            Name = "古いZIPをアーカイブ",
            Enabled = false,
            WatchFolder = @"C:\Users\demo\Downloads",
            Conditions = new List<RuleCondition>
            {
                new() { Type = "extension", Operator = "equals", Value = ".zip" },
                new() { Type = "days_old", Operator = "greater_than", Value = "30" }
            },
            Actions = new List<RuleAction>
            {
                new() { Type = "move", Destination = @"D:\Archive\Downloads" }
            }
        }
    };

    private static IReadOnlyList<HistoryRecord> CreateHistory()
    {
        DateTime now = DateTime.UtcNow;
        return new[]
        {
            new HistoryRecord
            {
                Id = 101, OperationId = "demo101", OpType = OperationType.Move,
                SourcePath = @"C:\Users\demo\Downloads\scan_0041.pdf",
                DestinationPath = @"C:\Users\demo\Documents\請求書\2026-08-25_テックサプライ_請求書.pdf",
                State = OperationState.Completed, FileSizeBytes = 2_482_100,
                CreatedAtUtc = now.AddMinutes(-8), UpdatedAtUtc = now.AddMinutes(-8)
            },
            new HistoryRecord
            {
                Id = 100, OperationId = "demo100", OpType = OperationType.Rename,
                SourcePath = @"C:\Users\demo\Downloads\receipt.jpg",
                DestinationPath = @"C:\Users\demo\Downloads\2026-08-24_カフェ_領収書.jpg",
                State = OperationState.Completed, FileSizeBytes = 814_230,
                CreatedAtUtc = now.AddHours(-1), UpdatedAtUtc = now.AddHours(-1)
            },
            new HistoryRecord
            {
                Id = 99, OperationId = "demo099", OpType = OperationType.Copy,
                SourcePath = @"C:\Users\demo\Downloads\meeting-notes.txt",
                DestinationPath = @"C:\Users\demo\Documents\Notes\meeting-notes.txt",
                State = OperationState.Failed, ErrorMessage = "移動先へのアクセス権がありません",
                FileSizeBytes = 12_490, CreatedAtUtc = now.AddHours(-3), UpdatedAtUtc = now.AddHours(-3)
            },
            new HistoryRecord
            {
                Id = 98, OperationId = "demo098", OpType = OperationType.Move,
                SourcePath = @"C:\Users\demo\Downloads\IMG_3819.png",
                DestinationPath = @"C:\Users\demo\Pictures\Screenshots\IMG_3819.png",
                State = OperationState.Undone, FileSizeBytes = 1_640_300,
                CreatedAtUtc = now.AddDays(-1), UpdatedAtUtc = now.AddHours(-20)
            }
        };
    }

    private static AppSettings CloneSettings(AppSettings source) => new()
    {
        WatchFolders = source.WatchFolders.Select(folder => new WatchFolderSetting
        {
            Path = folder.Path,
            Enabled = folder.Enabled,
            IncludeSubdirectories = folder.IncludeSubdirectories
        }).ToList(),
        StabilityCheckIntervalMs = source.StabilityCheckIntervalMs,
        PeriodicScanIntervalHours = source.PeriodicScanIntervalHours,
        ApplyAllMatchingRules = source.ApplyAllMatchingRules,
        IsQuickLookEnabled = source.IsQuickLookEnabled,
        QuickLookShortcut = source.QuickLookShortcut,
        PythonPort = source.PythonPort,
        UsePreloadedSlmModel = source.UsePreloadedSlmModel,
        SlmModelPath = source.SlmModelPath,
        EnableToastNotifications = source.EnableToastNotifications,
        WalCheckpointIntervalMinutes = source.WalCheckpointIntervalMinutes,
        SchemaVersion = source.SchemaVersion
    };

    private static List<RuleModel> CloneRules(IEnumerable<RuleModel> source) => source.Select(rule => new RuleModel
    {
        Id = rule.Id,
        Name = rule.Name,
        Enabled = rule.Enabled,
        WatchFolder = rule.WatchFolder,
        Conditions = rule.Conditions.Select(condition => new RuleCondition
        {
            Type = condition.Type,
            Operator = condition.Operator,
            Value = condition.Value switch
            {
                string[] values => values.ToArray(),
                IEnumerable<string> values => values.ToArray(),
                _ => condition.Value,
            }
        }).ToList(),
        Actions = rule.Actions.Select(action => new RuleAction
        {
            Type = action.Type,
            Destination = action.Destination,
            Pattern = action.Pattern
        }).ToList()
    }).ToList();
}
