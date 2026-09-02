using System.Collections.ObjectModel;
using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Mvvm;
using FileOrganizer.UI.Services;

namespace FileOrganizer.UI.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IFrontendBackendGateway _gateway;
    private readonly Action<string> _showMessage;
    private int _stabilityCheckIntervalMs = 750;
    private int _periodicScanIntervalHours = 24;
    private bool _applyAllMatchingRules;
    private bool _isQuickLookEnabled = true;
    private string _quickLookShortcut = "Space";
    private int _pythonPort;
    private bool _usePreloadedSlmModel;
    private string _slmModelPath = string.Empty;
    private bool _enableToastNotifications = true;
    private int _walCheckpointIntervalMinutes = 60;

    public SettingsViewModel(IFrontendBackendGateway gateway, Action<string> showMessage)
    {
        _gateway = gateway;
        _showMessage = showMessage;
        AddFolderCommand = new RelayCommand(() => WatchFolders.Add(new WatchFolderItemViewModel(new WatchFolderSetting
        {
            Path = @"C:\Users\demo\Documents\Inbox",
            Enabled = true
        })));
        RemoveFolderCommand = new RelayCommand(parameter =>
        {
            if (parameter is WatchFolderItemViewModel folder)
                WatchFolders.Remove(folder);
        });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
    }

    public ObservableCollection<WatchFolderItemViewModel> WatchFolders { get; } = new();
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

    public int StabilityCheckIntervalMs { get => _stabilityCheckIntervalMs; set => SetProperty(ref _stabilityCheckIntervalMs, value); }
    public int PeriodicScanIntervalHours { get => _periodicScanIntervalHours; set => SetProperty(ref _periodicScanIntervalHours, value); }
    public bool ApplyAllMatchingRules { get => _applyAllMatchingRules; set => SetProperty(ref _applyAllMatchingRules, value); }
    public bool IsQuickLookEnabled { get => _isQuickLookEnabled; set => SetProperty(ref _isQuickLookEnabled, value); }
    public string QuickLookShortcut { get => _quickLookShortcut; set => SetProperty(ref _quickLookShortcut, value); }
    public int PythonPort { get => _pythonPort; set => SetProperty(ref _pythonPort, value); }
    public bool UsePreloadedSlmModel { get => _usePreloadedSlmModel; set => SetProperty(ref _usePreloadedSlmModel, value); }
    public string SlmModelPath { get => _slmModelPath; set => SetProperty(ref _slmModelPath, value); }
    public bool EnableToastNotifications { get => _enableToastNotifications; set => SetProperty(ref _enableToastNotifications, value); }
    public int WalCheckpointIntervalMinutes { get => _walCheckpointIntervalMinutes; set => SetProperty(ref _walCheckpointIntervalMinutes, value); }

    public void Load(AppSettings settings)
    {
        WatchFolders.Clear();
        foreach (var folder in settings.WatchFolders)
            WatchFolders.Add(new WatchFolderItemViewModel(folder));

        StabilityCheckIntervalMs = settings.StabilityCheckIntervalMs;
        PeriodicScanIntervalHours = settings.PeriodicScanIntervalHours;
        ApplyAllMatchingRules = settings.ApplyAllMatchingRules;
        IsQuickLookEnabled = settings.IsQuickLookEnabled;
        QuickLookShortcut = settings.QuickLookShortcut;
        PythonPort = settings.PythonPort;
        UsePreloadedSlmModel = settings.UsePreloadedSlmModel;
        SlmModelPath = settings.SlmModelPath;
        EnableToastNotifications = settings.EnableToastNotifications;
        WalCheckpointIntervalMinutes = settings.WalCheckpointIntervalMinutes;
    }

    private async Task SaveAsync()
    {
        var settings = new AppSettings
        {
            WatchFolders = WatchFolders.Select(folder => folder.ToModel()).ToList(),
            StabilityCheckIntervalMs = Math.Clamp(StabilityCheckIntervalMs, 500, 5000),
            PeriodicScanIntervalHours = Math.Clamp(PeriodicScanIntervalHours, 1, 168),
            ApplyAllMatchingRules = ApplyAllMatchingRules,
            IsQuickLookEnabled = IsQuickLookEnabled,
            QuickLookShortcut = QuickLookShortcut.Trim(),
            PythonPort = Math.Clamp(PythonPort, 0, 65535),
            UsePreloadedSlmModel = UsePreloadedSlmModel,
            SlmModelPath = SlmModelPath.Trim(),
            EnableToastNotifications = EnableToastNotifications,
            WalCheckpointIntervalMinutes = Math.Clamp(WalCheckpointIntervalMinutes, 5, 1440),
            SchemaVersion = 1
        };

        try
        {
            await _gateway.SaveSettingsAsync(settings);
            _showMessage(_gateway.IsBackendConnected
                ? "設定を保存しました。"
                : "設定をUI確認用メモリへ保存しました（OS設定は変更していません）。");
        }
        catch (Exception ex)
        {
            _showMessage($"設定を保存できませんでした: {ex.Message}");
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            var result = await _gateway.ExportDiagnosticsAsync();
            _showMessage(result.Message);
        }
        catch (Exception ex)
        {
            _showMessage($"診断ログを出力できませんでした: {ex.Message}");
        }
    }
}

public sealed class WatchFolderItemViewModel : ObservableObject
{
    private string _path;
    private bool _enabled;
    private bool _includeSubdirectories;

    public WatchFolderItemViewModel(WatchFolderSetting model)
    {
        _path = model.Path;
        _enabled = model.Enabled;
        _includeSubdirectories = model.IncludeSubdirectories;
    }

    public string Path { get => _path; set => SetProperty(ref _path, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool IncludeSubdirectories { get => _includeSubdirectories; set => SetProperty(ref _includeSubdirectories, value); }

    public WatchFolderSetting ToModel() => new()
    {
        Path = Path.Trim(),
        Enabled = Enabled,
        IncludeSubdirectories = IncludeSubdirectories
    };
}
