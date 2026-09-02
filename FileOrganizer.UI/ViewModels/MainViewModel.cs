using System.Collections.ObjectModel;
using System.Windows.Threading;
using FileOrganizer.UI.Models;
using FileOrganizer.UI.Mvvm;
using FileOrganizer.UI.Services;

namespace FileOrganizer.UI.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IFrontendBackendGateway _gateway;
    private readonly DispatcherTimer _messageTimer;
    private object? _currentPage;
    private string _currentPageKey = "dashboard";
    private string _message = string.Empty;
    private bool _isMessageVisible;
    private bool _isInitializing = true;

    public MainViewModel(IFrontendBackendGateway gateway)
    {
        _gateway = gateway;
        _gateway.ActivityOccurred += OnBackendActivity;

        Dashboard = new DashboardViewModel(gateway, ShowMessage);
        Rules = new RulesViewModel(gateway, ShowMessage);
        History = new HistoryViewModel(gateway, ShowMessage);
        Settings = new SettingsViewModel(gateway, ShowMessage);
        Dashboard.NavigationRequested += key => Navigate(key);

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new("dashboard", "⌂", "ホーム"),
            new("rules", "◇", "整理ルール"),
            new("history", "↺", "実行履歴"),
            new("settings", "⚙", "設定")
        };

        NavigateCommand = new RelayCommand(Navigate);
        DismissMessageCommand = new RelayCommand(() => IsMessageVisible = false);
        CurrentPage = Dashboard;
        UpdateNavigationSelection();

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _messageTimer.Tick += (_, _) =>
        {
            IsMessageVisible = false;
            _messageTimer.Stop();
        };
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }
    public DashboardViewModel Dashboard { get; }
    public RulesViewModel Rules { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand DismissMessageCommand { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageKey
    {
        get => _currentPageKey;
        private set => SetProperty(ref _currentPageKey, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsMessageVisible
    {
        get => _isMessageVisible;
        private set => SetProperty(ref _isMessageVisible, value);
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set => SetProperty(ref _isInitializing, value);
    }

    public bool IsBackendConnected => _gateway.IsBackendConnected;
    public string ConnectionLabel => IsBackendConnected ? "バックエンド接続済み" : "UI確認モード";

    public DryRunViewModel CreateDryRunViewModel(string folderPath) => new(_gateway, folderPath);
    public DryRunViewModel CreateDryRunViewModel(IReadOnlyList<string> filePaths) => new(_gateway, filePaths);

    public async Task RefreshRuntimeAsync()
    {
        FrontendSnapshot snapshot = await _gateway.LoadAsync();
        Dashboard.Load(snapshot);
        History.Load(snapshot.RecentHistory);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            IsInitializing = true;
            var snapshot = await _gateway.LoadAsync(ct);
            Dashboard.Load(snapshot);
            Rules.Load(snapshot.Rules);
            History.Load(snapshot.RecentHistory);
            Settings.Load(snapshot.Settings);
            OnPropertyChanged(nameof(IsBackendConnected));
            OnPropertyChanged(nameof(ConnectionLabel));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // アプリ終了中。画面通知は不要。
        }
        catch (Exception ex)
        {
            ShowMessage($"画面データを読み込めませんでした: {ex.Message}");
        }
        finally
        {
            IsInitializing = false;
        }
    }

    public void ShowMessage(string message)
    {
        Message = message;
        IsMessageVisible = true;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void Navigate(object? parameter)
    {
        string? key = parameter switch
        {
            NavigationItemViewModel item => item.Key,
            string rawKey => rawKey,
            _ => null
        };

        if (key is null)
            return;

        CurrentPageKey = key;
        CurrentPage = key switch
        {
            "rules" => Rules,
            "history" => History,
            "settings" => Settings,
            _ => Dashboard
        };
        UpdateNavigationSelection();
    }

    private void UpdateNavigationSelection()
    {
        foreach (var item in NavigationItems)
            item.IsSelected = string.Equals(item.Key, CurrentPageKey, StringComparison.Ordinal);
    }

    private void OnBackendActivity(object? sender, BackendActivityEventArgs e)
    {
        System.Windows.Application? application = System.Windows.Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted) return;
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!string.IsNullOrWhiteSpace(e.Message)) ShowMessage(e.Message);
            try { await RefreshRuntimeAsync(); }
            catch (Exception ex) { ShowMessage($"画面の状態を更新できませんでした: {ex.Message}"); }
        }));
    }
}

public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavigationItemViewModel(string key, string icon, string label)
    {
        Key = key;
        Icon = icon;
        Label = label;
    }

    public string Key { get; }
    public string Icon { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
