using System.ComponentModel;
using System.Windows;
using FileOrganizer.UI.Services;
using FileOrganizer.UI.ViewModels;
using FileOrganizer.Widgets.DropZone;
using FileOrganizer.Widgets.Tray;

namespace FileOrganizer.UI;

public partial class App : Application
{
    private IFrontendBackendGateway? _backendGateway;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private TrayIconManager? _trayIcon;
    private DropShelfWindow? _dropShelfWindow;
    private readonly CancellationTokenSource _applicationCancellation = new();
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI開発段階では実ファイル、SQLite、Pythonプロセスへ触れないGatewayを使用する。
        // バックエンド統合時は ProductionBackendGateway を実装してこの1行だけ差し替える。
        _backendGateway = new DesignTimeBackendGateway();

        _mainViewModel = new MainViewModel(_backendGateway);
        _mainWindow = new MainWindow(_mainViewModel);
        MainWindow = _mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Show();

        InitializeWidgets();

        await _mainViewModel.InitializeAsync(_applicationCancellation.Token);
        if (!_applicationCancellation.IsCancellationRequested)
            _trayIcon?.SetMonitoringState(_mainViewModel.Dashboard.IsMonitoring);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _applicationCancellation.Cancel();
        if (_mainViewModel is not null)
            _mainViewModel.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        if (_dropShelfWindow is not null)
        {
            _dropShelfWindow.FilesSubmitted -= OnDroppedFilesSubmitted;
            _dropShelfWindow.Close();
        }
        _trayIcon?.Dispose();
        (_backendGateway as IDisposable)?.Dispose();
        _applicationCancellation.Dispose();
        base.OnExit(e);
    }

    private void InitializeWidgets()
    {
        if (_mainViewModel is null)
            return;

        try
        {
            _trayIcon = new TrayIconManager();
            _trayIcon.OpenRequested += (_, _) => ShowMainWindow();
            _trayIcon.DryRunRequested += (_, _) =>
            {
                ShowMainWindow();
                _mainViewModel.Dashboard.RequestDryRunCommand.Execute(null);
            };
            _trayIcon.DropZoneRequested += (_, _) => ShowDropZone();
            _trayIcon.MonitoringToggleRequested += (_, args) =>
            {
                if (_mainViewModel.Dashboard.IsMonitoring != args.Enabled)
                    _mainViewModel.Dashboard.ToggleMonitoringCommand.Execute(null);
            };
            _trayIcon.ExitRequested += (_, _) => ExitApplication();
            _mainViewModel.Dashboard.DropZoneRequested += (_, _) => ShowDropZone();
            _mainViewModel.Dashboard.PropertyChanged += OnDashboardPropertyChanged;
        }
        catch (Exception ex)
        {
            // Explorer/通知領域が利用できない環境でもメインUIは継続する。
            _mainViewModel.ShowMessage($"タスクトレイを初期化できませんでした: {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowDropZone()
    {
        if (_dropShelfWindow is null)
        {
            _dropShelfWindow = new DropShelfWindow();
            _dropShelfWindow.FilesSubmitted += OnDroppedFilesSubmitted;
            _dropShelfWindow.Closed += OnDropShelfClosed;
        }

        _dropShelfWindow.Show();
        _dropShelfWindow.Activate();
    }

    private void OnDroppedFilesSubmitted(object? sender, DroppedFilesSubmittedEventArgs e)
    {
        // Productionではファイル単位のDry Run Gatewayへ渡す。現段階では承認イベントの確認だけを行う。
        _mainViewModel?.ShowMessage($"{e.Paths.Count}件を受け取りました。バックエンド接続後に整理内容を計算します。");
        ShowMainWindow();
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsMonitoring) && _mainViewModel is not null)
            _trayIcon?.SetMonitoringState(_mainViewModel.Dashboard.IsMonitoring);
    }

    private void OnDropShelfClosed(object? sender, EventArgs e)
    {
        if (_dropShelfWindow is null)
            return;
        _dropShelfWindow.FilesSubmitted -= OnDroppedFilesSubmitted;
        _dropShelfWindow.Closed -= OnDropShelfClosed;
        _dropShelfWindow = null;
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;

        if (_trayIcon is null)
        {
            // トレイ初期化に失敗した環境では、画面を閉じられなくなる事態を避けて通常終了する。
            _isExiting = true;
            Dispatcher.BeginInvoke(new Action(Shutdown));
            return;
        }

        // 常駐仕様: タイトルバーの×は終了ではなくトレイへ格納する。
        e.Cancel = true;
        _mainWindow?.Hide();
        _trayIcon?.ShowNotification("File Organizer", "フォルダの監視を続けています。終了はトレイメニューから行えます。");
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _applicationCancellation.Cancel();
        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Close();
        }
        Shutdown();
    }
}
