using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Models;

namespace FileOrganizer.UI.Services;

/// <summary>
/// WPF ViewModelからバックエンド実装を隔離する唯一の境界。
/// </summary>
/// <remarks>
/// Production実装での接続先:
/// LoadAsync             -> ISettingsRepository.LoadSettingsAsync/LoadRulesAsync + IHistoryRepository.GetRecentAsync
/// SaveRulesAsync        -> ISettingsRepository.SaveRulesAsync
/// SaveSettingsAsync     -> ISettingsRepository.SaveSettingsAsync
/// SetMonitoringAsync    -> IWatcherService.StartAsync/StopAsync
/// PreviewCleanupAsync   -> Core.Engine.DryRunSimulator.SimulateFolderAsync
/// ExecuteCleanupAsync   -> ProcessingCoordinator.ProcessAsync（Dry Run再検証後、選択項目のみ）
/// UndoAsync             -> IUndoManager.UndoAsync
/// ExportDiagnosticsAsync-> Widgets側の個人情報マスク済み診断ログサービス
///
/// ViewModelはCoreの具象クラスやPythonプロセスを直接参照しない。これにより、長時間処理を
/// UIスレッド外で待機しつつ、キャンセルとエラー表示を一箇所で扱える。
/// Production Composition RootはApp起動時にDatabaseInitializer→StartupRecoveryService→
/// PythonProcessManager（stdoutハンドシェイク後にIPythonApiClient.Configure）の順で初期化し、
/// すべて成功してからIWatcherServiceを開始する。終了時はWatcher停止後にPython Job Objectを破棄する。
/// </remarks>
public interface IFrontendBackendGateway
{
    bool IsBackendConnected { get; }

    Task<FrontendSnapshot> LoadAsync(CancellationToken ct = default);
    Task SaveRulesAsync(IReadOnlyList<RuleModel> rules, CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task<FrontendActionResult> SetMonitoringAsync(bool enabled, CancellationToken ct = default);
    Task<IReadOnlyList<DryRunPreviewItem>> PreviewCleanupAsync(string folderPath, CancellationToken ct = default);
    Task<FrontendActionResult> ExecuteCleanupAsync(IReadOnlyList<DryRunPreviewItem> approvedItems, CancellationToken ct = default);
    Task<UndoResult> UndoAsync(long historyRecordId, CancellationToken ct = default);
    Task<FrontendActionResult> ExportDiagnosticsAsync(CancellationToken ct = default);
}
