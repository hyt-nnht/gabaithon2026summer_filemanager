using System.Collections.ObjectModel;
using FileOrganizer.UI.Models;
using FileOrganizer.UI.Mvvm;
using FileOrganizer.UI.Services;

namespace FileOrganizer.UI.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IFrontendBackendGateway _gateway;
    private readonly Action<string> _showMessage;
    private bool _isMonitoring;
    private int _organizedToday;
    private int _pendingFiles;
    private string _aiStatus = "準備中";
    private string _primaryWatchFolder = "監視フォルダを設定してください";
    private string _lastProcessedLabel = "まだ処理はありません";

    public DashboardViewModel(IFrontendBackendGateway gateway, Action<string> showMessage)
    {
        _gateway = gateway;
        _showMessage = showMessage;
        ToggleMonitoringCommand = new AsyncRelayCommand(ToggleMonitoringAsync);
        RequestDryRunCommand = new RelayCommand(() => DryRunRequested?.Invoke(this, EventArgs.Empty));
        ShowDropZoneCommand = new RelayCommand(() => DropZoneRequested?.Invoke(this, EventArgs.Empty));
        OpenRulesCommand = new RelayCommand(() => NavigationRequested?.Invoke("rules"));
    }

    public event EventHandler? DryRunRequested;
    public event EventHandler? DropZoneRequested;
    public event Action<string>? NavigationRequested;

    public ObservableCollection<HistoryItemViewModel> RecentItems { get; } = new();
    public AsyncRelayCommand ToggleMonitoringCommand { get; }
    public RelayCommand RequestDryRunCommand { get; }
    public RelayCommand ShowDropZoneCommand { get; }
    public RelayCommand OpenRulesCommand { get; }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                OnPropertyChanged(nameof(MonitoringTitle));
                OnPropertyChanged(nameof(MonitoringDescription));
                OnPropertyChanged(nameof(MonitoringButtonLabel));
            }
        }
    }

    public string MonitoringTitle => IsMonitoring ? "フォルダを見守っています" : "監視を一時停止しています";
    public string MonitoringDescription => IsMonitoring
        ? "新しいファイルは安定確認後、安全な整理キューへ送られます。"
        : "新しいファイルは処理されません。既存ルールと履歴は保持されます。";
    public string MonitoringButtonLabel => IsMonitoring ? "監視を一時停止" : "監視を再開";

    public int OrganizedToday
    {
        get => _organizedToday;
        private set => SetProperty(ref _organizedToday, value);
    }

    public int PendingFiles
    {
        get => _pendingFiles;
        private set => SetProperty(ref _pendingFiles, value);
    }

    public string AiStatus
    {
        get => _aiStatus;
        private set => SetProperty(ref _aiStatus, value);
    }

    public string PrimaryWatchFolder
    {
        get => _primaryWatchFolder;
        private set => SetProperty(ref _primaryWatchFolder, value);
    }

    public string LastProcessedLabel
    {
        get => _lastProcessedLabel;
        private set => SetProperty(ref _lastProcessedLabel, value);
    }

    public void Load(FrontendSnapshot snapshot)
    {
        IsMonitoring = snapshot.Monitoring.IsMonitoring;
        OrganizedToday = snapshot.Monitoring.OrganizedToday;
        PendingFiles = snapshot.Monitoring.PendingFiles;
        AiStatus = snapshot.Monitoring.AiStatus;
        PrimaryWatchFolder = snapshot.Settings.WatchFolders.FirstOrDefault(folder => folder.Enabled)?.Path
            ?? "有効な監視フォルダがありません";
        LastProcessedLabel = snapshot.Monitoring.LastProcessedAt is { } last
            ? $"最終処理  {last.LocalDateTime:HH:mm}"
            : "まだ処理はありません";

        RecentItems.Clear();
        foreach (var record in snapshot.RecentHistory.Take(3))
            RecentItems.Add(new HistoryItemViewModel(record));
    }

    private async Task ToggleMonitoringAsync()
    {
        bool nextValue = !IsMonitoring;
        try
        {
            var result = await _gateway.SetMonitoringAsync(nextValue);

            // UI確認モードでも状態遷移は見せるが、GatewayはOS監視を開始・停止しない。
            IsMonitoring = nextValue;
            _showMessage(result.Message);
        }
        catch (Exception ex)
        {
            _showMessage($"監視状態を変更できませんでした: {ex.Message}");
        }
    }
}
