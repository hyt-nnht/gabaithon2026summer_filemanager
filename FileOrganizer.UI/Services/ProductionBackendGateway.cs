using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileOrganizer.Core.Client;
using FileOrganizer.Core.Database;
using FileOrganizer.Core.Engine;
using FileOrganizer.Core.Services;
using FileOrganizer.Core.Utils;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Core.Win32;
using FileOrganizer.Infrastructure.Ocr;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Models;

namespace FileOrganizer.UI.Services;

/// <summary>
/// WPFの操作を既存Core/Infrastructureへ接続するComposition Root。
/// 画面コードからSQLite・Watcher・OCR・Python・ファイル操作の具象型を隠す。
/// </summary>
public sealed class ProductionBackendGateway : IFrontendBackendGateway, IAsyncDisposable
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly JsonSettingsRepository _settingsRepository = new();

    private SqliteHistoryRepository? _historyRepository;
    private WatcherService? _watcherService;
    private ProcessingCoordinator? _coordinator;
    private DryRunSimulator? _dryRunSimulator;
    private UndoManager? _undoManager;
    private WalMaintenanceService? _walMaintenance;
    private WindowsMediaOcrService? _ocrService;
    private PythonApiClient? _pythonApiClient;
    private PythonServiceSupervisor? _pythonSupervisor;
    private ModelDownloadManager? _modelDownloadManager;
    private JobObjectManager? _jobObjectManager;
    private AppSettings? _settings;
    private string? _connectionString;
    private bool _initialized;
    private bool _monitoring;
    private bool _disposed;
    private string _aiStatus = "ローカルAI 未接続（基本ルールは利用可能）";

    public event EventHandler<BackendActivityEventArgs>? ActivityOccurred;

    public bool IsBackendConnected => _initialized && !_disposed;

    public async Task<FrontendSnapshot> LoadAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        IReadOnlyList<HistoryRecord> history = await LoadHistoryAsync(ct).ConfigureAwait(false);
        List<RuleModel> rules = await _settingsRepository.LoadRulesAsync(ct).ConfigureAwait(false);

        int organizedToday = history.Count(record =>
            record.State == OperationState.Completed && record.UpdatedAtUtc.ToLocalTime().Date == DateTime.Today);
        DateTimeOffset? lastProcessed = history.Count > 0
            ? new DateTimeOffset(history.Max(record => record.UpdatedAtUtc.ToLocalTime()))
            : null;

        return new FrontendSnapshot(
            _settings!,
            rules,
            history,
            new MonitoringSnapshot(
                _monitoring,
                _watcherService?.PendingCount ?? 0,
                organizedToday,
                lastProcessed,
                _aiStatus));
    }

    public async Task SaveRulesAsync(IReadOnlyList<RuleModel> rules, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _settingsRepository.SaveRulesAsync(rules.ToList(), ct).ConfigureAwait(false);
        ActivityOccurred?.Invoke(this, new BackendActivityEventArgs("整理ルールを保存しました。"));
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _settingsRepository.SaveSettingsAsync(settings, ct).ConfigureAwait(false);

        await _processingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool restartMonitoring = _monitoring;
            bool restartPython = _settings is null ||
                _settings.UsePreloadedSlmModel != settings.UsePreloadedSlmModel ||
                !string.Equals(_settings.SlmModelPath, settings.SlmModelPath, StringComparison.OrdinalIgnoreCase);
            bool restartWalMaintenance = _settings?.WalCheckpointIntervalMinutes != settings.WalCheckpointIntervalMinutes;
            if (restartMonitoring && _watcherService is not null)
            {
                await _watcherService.StopAsync(ct).ConfigureAwait(false);
            }

            _settings = settings;
            if (restartWalMaintenance && _connectionString is not null)
            {
                _walMaintenance?.Dispose();
                _walMaintenance = new WalMaintenanceService(
                    _connectionString,
                    Math.Max(1, settings.WalCheckpointIntervalMinutes));
            }
            if (restartPython)
            {
                await StopPythonAsync().ConfigureAwait(false);
                await TryStartPythonAsync(settings, ct).ConfigureAwait(false);
            }
            await RebuildProcessingPipelineAsync(settings).ConfigureAwait(false);
            if (restartMonitoring)
            {
                await _watcherService!.StartAsync(settings.WatchFolders, ct).ConfigureAwait(false);
            }

            ActivityOccurred?.Invoke(this, new BackendActivityEventArgs("設定を保存して監視構成へ反映しました。"));
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public async Task<FrontendActionResult> SetMonitoringAsync(bool enabled, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (enabled)
        {
            await _watcherService!.StartAsync(_settings!.WatchFolders, ct).ConfigureAwait(false);
            _monitoring = true;
            return FrontendActionResult.Completed("フォルダ監視を開始しました。起動前からあるファイルは自動処理しません。");
        }

        await _watcherService!.StopAsync(ct).ConfigureAwait(false);
        _monitoring = false;
        return FrontendActionResult.Completed("フォルダ監視を一時停止しました。");
    }

    public async Task<IReadOnlyList<HistoryRecord>> LoadHistoryAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await _historyRepository!.GetRecentAsync(200, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DryRunPreviewItem>> PreviewCleanupAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        List<RuleModel> rules = await _settingsRepository.LoadRulesAsync(ct).ConfigureAwait(false);
        bool includeSubdirectories = _settings!.WatchFolders.FirstOrDefault(folder =>
            PathsEqual(folder.Path, folderPath))?.IncludeSubdirectories ?? false;
        IReadOnlyList<DryRunPlanEntry> plans = await _dryRunSimulator!.SimulateFolderAsync(
            folderPath,
            includeSubdirectories,
            rules,
            _settings!.ApplyAllMatchingRules,
            ct).ConfigureAwait(false);
        return MapPlans(plans);
    }

    public async Task<IReadOnlyList<DryRunPreviewItem>> PreviewFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        List<RuleModel> rules = await _settingsRepository.LoadRulesAsync(ct).ConfigureAwait(false);
        List<FileMetadata> metadata = filePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Where(path => !string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            .Select(CreateMetadata)
            .ToList();

        IReadOnlyList<DryRunPlanEntry> plans = await _dryRunSimulator!.SimulateFilesAsync(
            metadata,
            rules,
            _settings!.ApplyAllMatchingRules,
            ct).ConfigureAwait(false);
        return MapPlans(plans);
    }

    public async Task<FrontendActionResult> ExecuteCleanupAsync(
        IReadOnlyList<DryRunPreviewItem> approvedItems,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approvedItems);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (approvedItems.Count == 0)
        {
            return FrontendActionResult.Deferred("実行対象が選択されていません。");
        }
        if (approvedItems.Any(item => item.RequiresConfirmation))
        {
            return FrontendActionResult.Deferred("確認が必要な項目が含まれます。競合を解消してDry Runをやり直してください。");
        }
        if (approvedItems.GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            return FrontendActionResult.Deferred("同じ元ファイルが重複して選択されています。Dry Runをやり直してください。");
        }

        await _processingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // TOCTOU対策: 1件も変更する前に、全対象のサイズ・更新日時・軽量ハッシュ・ルール結果を再計算する。
            IReadOnlyList<DryRunPreviewItem> fresh = await PreviewFilesAsync(
                approvedItems.Select(item => item.SourcePath).ToList(), ct).ConfigureAwait(false);
            Dictionary<string, DryRunPreviewItem> freshByPath = fresh.ToDictionary(
                item => item.SourcePath, StringComparer.OrdinalIgnoreCase);
            foreach (DryRunPreviewItem approved in approvedItems)
            {
                if (!freshByPath.TryGetValue(approved.SourcePath, out DryRunPreviewItem? current) ||
                    !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(approved.PlanSignature),
                        Encoding.UTF8.GetBytes(current.PlanSignature)))
                {
                    return FrontendActionResult.Deferred(
                        $"Dry Run後にファイルまたはルールが変わりました。再確認してください: {Path.GetFileName(approved.SourcePath)}");
                }
            }

            int succeededFiles = 0;
            foreach (DryRunPreviewItem item in approvedItems)
            {
                ct.ThrowIfCancellationRequested();
                if (!MatchesApprovedSource(item))
                {
                    string changedMessage = $"実行直前にファイルの変更を検出したため中断しました: {Path.GetFileName(item.SourcePath)}";
                    ActivityOccurred?.Invoke(this, new BackendActivityEventArgs(changedMessage));
                    return FrontendActionResult.Deferred(changedMessage);
                }
                IReadOnlyList<HistoryRecord> records = await _coordinator!
                    .ProcessAsync(CreateMetadata(item.SourcePath), ct)
                    .ConfigureAwait(false);
                if (records.Count > 0 && records.All(record => record.State == OperationState.Completed))
                {
                    succeededFiles++;
                }
            }

            string message = $"{approvedItems.Count}件中{succeededFiles}件の整理が完了しました。";
            ActivityOccurred?.Invoke(this, new BackendActivityEventArgs(message));
            return succeededFiles == approvedItems.Count
                ? FrontendActionResult.Completed(message)
                : FrontendActionResult.Deferred(message + " 詳細は実行履歴を確認してください。");
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public async Task<UndoResult> UndoAsync(long historyRecordId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _processingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UndoResult result = await _undoManager!.UndoAsync(historyRecordId, ct).ConfigureAwait(false);
            ActivityOccurred?.Invoke(this, new BackendActivityEventArgs(result.Message));
            return result;
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public async Task<FrontendActionResult> ExportDiagnosticsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        IReadOnlyList<HistoryRecord> history = await LoadHistoryAsync(ct).ConfigureAwait(false);
        string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        Directory.CreateDirectory(outputFolder);
        string zipPath = Path.Combine(outputFolder, $"FileOrganizer-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        await using FileStream stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        ZipArchiveEntry entry = archive.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        await writer.WriteLineAsync($"generated_utc={DateTime.UtcNow:O}").ConfigureAwait(false);
        await writer.WriteLineAsync($"monitoring={_monitoring}").ConfigureAwait(false);
        await writer.WriteLineAsync($"ai_status={LogMasker.MaskPersonalInfo(_aiStatus)}").ConfigureAwait(false);
        await writer.WriteLineAsync($"history_count={history.Count}").ConfigureAwait(false);
        foreach (HistoryRecord record in history)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                $"id={record.Id};op={record.OpType};state={record.State};source={LogMasker.HashPath(record.SourcePath)};destination={LogMasker.HashPath(record.DestinationPath)};error={LogMasker.MaskPersonalInfo(record.ErrorMessage)}")
                .ConfigureAwait(false);
        }

        return FrontendActionResult.Completed($"個人情報をマスクした診断ログを出力しました: {zipPath}");
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (!_initialized) return;
        if (_watcherService is not null)
        {
            await _watcherService.StopAsync(ct).ConfigureAwait(false);
        }
        _monitoring = false;
        if (_walMaintenance is not null)
        {
            try { await _walMaintenance.RunCheckpointNowAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;

        await _initializationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _settings = await _settingsRepository.LoadSettingsAsync(ct).ConfigureAwait(false);
            _connectionString = DatabaseInitializer.BuildConnectionString(DatabaseInitializer.GetDefaultDatabaseFilePath());
            await new DatabaseInitializer(_connectionString).InitializeAsync(ct).ConfigureAwait(false);
            _historyRepository = new SqliteHistoryRepository(_connectionString);
            await new StartupRecoveryService(_historyRepository).PerformStartupRecoveryAsync().ConfigureAwait(false);
            _walMaintenance = new WalMaintenanceService(
                _connectionString,
                Math.Max(1, _settings.WalCheckpointIntervalMinutes));
            _ocrService = new WindowsMediaOcrService();

            await TryStartPythonAsync(_settings, ct).ConfigureAwait(false);
            await RebuildProcessingPipelineAsync(_settings).ConfigureAwait(false);

            // 起動時に既存ファイルは列挙しないため、監視開始自体は安全。
            await _watcherService!.StartAsync(_settings.WatchFolders, ct).ConfigureAwait(false);
            _monitoring = true;
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task TryStartPythonAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            string repositoryRoot = FindRepositoryRoot();
            string pythonExecutable = FindPythonExecutable(repositoryRoot);
            _jobObjectManager = new JobObjectManager();
            _pythonApiClient = new PythonApiClient();
            _modelDownloadManager = new ModelDownloadManager();
            _pythonSupervisor = new PythonServiceSupervisor(
                () => PythonProcessManager.CreateForPyService(_jobObjectManager, repositoryRoot, pythonExecutable),
                _pythonApiClient);
            _pythonSupervisor.ProcessCrashed += (_, _) =>
                ActivityOccurred?.Invoke(this, new BackendActivityEventArgs("ローカルAIが停止しました。次回解析時に1回再起動します。"));
            _pythonSupervisor.ServiceDegraded += (_, _) =>
            {
                _aiStatus = "ローカルAI 停止（基本ルールへ退避）";
                ActivityOccurred?.Invoke(this, new BackendActivityEventArgs(_aiStatus));
            };

            AppSettings runtimeSettings = PreparePythonSettings(settings, repositoryRoot);
            await _pythonSupervisor.StartAsync(runtimeSettings, _modelDownloadManager, null, ct).ConfigureAwait(false);
            bool healthy = await _pythonSupervisor.HealthCheckAsync(ct).ConfigureAwait(false);
            _aiStatus = healthy ? "ローカルAI 接続済み" : "ローカルAI 応答なし（基本ルールへ退避）";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _aiStatus = $"ローカルAI 起動失敗（基本ルールへ退避）: {ex.GetType().Name}";
            if (_pythonSupervisor is not null)
            {
                await _pythonSupervisor.DisposeAsync().ConfigureAwait(false);
                _pythonSupervisor = null;
            }
            _pythonApiClient?.Dispose();
            _pythonApiClient = null;
            _modelDownloadManager?.Dispose();
            _modelDownloadManager = null;
            _jobObjectManager?.Dispose();
            _jobObjectManager = null;
        }
    }

    private async Task StopPythonAsync()
    {
        if (_pythonSupervisor is not null)
        {
            await _pythonSupervisor.DisposeAsync().ConfigureAwait(false);
            _pythonSupervisor = null;
        }
        _pythonApiClient?.Dispose();
        _pythonApiClient = null;
        _modelDownloadManager?.Dispose();
        _modelDownloadManager = null;
        _jobObjectManager?.Dispose();
        _jobObjectManager = null;
        _aiStatus = "ローカルAI 未接続（基本ルールは利用可能）";
    }

    private async Task RebuildProcessingPipelineAsync(AppSettings settings)
    {
        if (_watcherService is not null)
        {
            _watcherService.FileStabilized -= OnFileStabilized;
            await _watcherService.DisposeAsync().ConfigureAwait(false);
        }

        _watcherService = new WatcherService(
            Math.Max(1, settings.StabilityCheckIntervalMs),
            Math.Max(1, settings.PeriodicScanIntervalHours));
        _watcherService.FileStabilized += OnFileStabilized;
        var fileOperations = new FileOperationService(_watcherService);
        var ruleEngine = new RuleEvaluator();
        IPythonApiClient? aiClient = _pythonSupervisor;
        _coordinator = new ProcessingCoordinator(
            ruleEngine,
            _historyRepository!,
            fileOperations,
            _settingsRepository,
            _ocrService,
            aiClient);
        _coordinator.ProcessingCompleted += (_, args) =>
            ActivityOccurred?.Invoke(this, new BackendActivityEventArgs(
                args.Records.Count == 0 ? null : $"{Path.GetFileName(args.SourceFullPath)} を整理しました。"));
        _dryRunSimulator = new DryRunSimulator(ruleEngine, ConflictPolicy.AutoRename, _ocrService, aiClient);
        _undoManager = new UndoManager(_historyRepository!, fileOperations);
    }

    private async void OnFileStabilized(object? sender, FileStableEventArgs e)
    {
        await _processingLock.WaitAsync().ConfigureAwait(false);
        try { await _coordinator!.ProcessAsync(e.Metadata).ConfigureAwait(false); }
        catch (Exception ex) { ActivityOccurred?.Invoke(this, new BackendActivityEventArgs($"自動整理に失敗しました: {ex.Message}")); }
        finally { _processingLock.Release(); }
    }

    private static IReadOnlyList<DryRunPreviewItem> MapPlans(IReadOnlyList<DryRunPlanEntry> plans)
    {
        var items = new List<DryRunPreviewItem>();
        foreach (DryRunPlanEntry plan in plans.Where(plan => plan.IsMatched && plan.Actions.Count > 0))
        {
            var info = new FileInfo(plan.SourcePath);
            List<DryRunPreviewAction> actions = plan.Actions.Select(action => new DryRunPreviewAction
            {
                OperationType = action.OpType,
                DestinationPath = action.PlannedDestinationPath,
                WillSkip = action.WillSkip,
                RequiresConfirmation = action.RequiresConfirmation,
            }).ToList();
            string note = actions.Any(action => action.RequiresConfirmation)
                ? "競合または設定不足のため、このままでは実行できません。"
                : actions.Any(action => action.WillSkip) ? "同名衝突ポリシーによりスキップ予定です。" : string.Empty;
            long size = info.Exists ? info.Length : 0;
            DateTime lastWrite = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            string lightweightHash = info.Exists ? HashHelper.ComputeLightweightHash(plan.SourcePath) : string.Empty;
            items.Add(new DryRunPreviewItem
            {
                SourcePath = plan.SourcePath,
                RuleName = plan.MatchedRuleName ?? string.Empty,
                Actions = actions,
                SourceSizeBytes = size,
                SourceLastWriteTimeUtc = lastWrite,
                SourceLightweightHash = lightweightHash,
                Note = note,
                PlanSignature = ComputePlanSignature(plan.SourcePath, size, lastWrite, lightweightHash, plan.MatchedRuleName, actions),
            });
        }
        return items;
    }

    private static string ComputePlanSignature(
        string sourcePath,
        long size,
        DateTime lastWriteUtc,
        string lightweightHash,
        string? ruleName,
        IEnumerable<DryRunPreviewAction> actions)
    {
        string actionText = string.Join('|', actions.Select(action =>
            $"{action.OperationType}:{action.DestinationPath}:{action.WillSkip}:{action.RequiresConfirmation}"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(sourcePath)}\n{size}\n{lastWriteUtc.Ticks}\n{lightweightHash}\n{ruleName}\n{actionText}"));
        return Convert.ToHexString(hash);
    }

    private static FileMetadata CreateMetadata(string path)
    {
        var info = new FileInfo(path);
        return new FileMetadata
        {
            FullPath = info.FullName,
            FileName = info.Name,
            Extension = info.Extension,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            CreatedTimeUtc = info.CreationTimeUtc,
        };
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool MatchesApprovedSource(DryRunPreviewItem approved)
    {
        try
        {
            var info = new FileInfo(approved.SourcePath);
            return info.Exists &&
                info.Length == approved.SourceSizeBytes &&
                info.LastWriteTimeUtc == approved.SourceLastWriteTimeUtc &&
                string.Equals(
                    HashHelper.ComputeLightweightHash(approved.SourcePath),
                    approved.SourceLightweightHash,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "py_service", "main.py")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("py_service/main.pyを含むリポジトリルートを見つけられませんでした。");
    }

    private static string FindPythonExecutable(string repositoryRoot)
    {
        string[] candidates =
        [
            Path.Combine(repositoryRoot, "py_service", ".venv", "Scripts", "python.exe"),
            Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe"),
            Path.Combine(repositoryRoot, "python", "python.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? "python";
    }

    private static AppSettings PreparePythonSettings(AppSettings source, string repositoryRoot)
    {
        if (!source.UsePreloadedSlmModel || string.IsNullOrWhiteSpace(source.SlmModelPath) || Path.IsPathFullyQualified(source.SlmModelPath))
        {
            return source;
        }

        return new AppSettings
        {
            WatchFolders = source.WatchFolders,
            StabilityCheckIntervalMs = source.StabilityCheckIntervalMs,
            PeriodicScanIntervalHours = source.PeriodicScanIntervalHours,
            ApplyAllMatchingRules = source.ApplyAllMatchingRules,
            IsQuickLookEnabled = source.IsQuickLookEnabled,
            QuickLookShortcut = source.QuickLookShortcut,
            PythonPort = source.PythonPort,
            UsePreloadedSlmModel = true,
            SlmModelPath = Path.GetFullPath(source.SlmModelPath, repositoryRoot),
            EnableToastNotifications = source.EnableToastNotifications,
            WalCheckpointIntervalMinutes = source.WalCheckpointIntervalMinutes,
            SchemaVersion = source.SchemaVersion,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ShutdownAsync().ConfigureAwait(false);
        _disposed = true;
        if (_watcherService is not null) await _watcherService.DisposeAsync().ConfigureAwait(false);
        await StopPythonAsync().ConfigureAwait(false);
        _walMaintenance?.Dispose();
        _initializationLock.Dispose();
    }
}
