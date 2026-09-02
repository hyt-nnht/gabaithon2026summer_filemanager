using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FileOrganizer.Widgets.Tray;

/// <summary>
/// タスクトレイの表示と、ユーザー操作の通知だけを担当する。
/// 監視開始・Dry Run・終了処理そのものは実行ホスト（FileOrganizer.UI）へイベントで委譲する。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _monitoringMenuItem;
    private bool _isMonitoring = true;
    private bool _disposed;

    public TrayIconManager()
    {
        var menu = new Forms.ContextMenuStrip();
        var openMenuItem = new Forms.ToolStripMenuItem("File Organizer を開く");
        var dryRunMenuItem = new Forms.ToolStripMenuItem("今すぐ整理…");
        var dropZoneMenuItem = new Forms.ToolStripMenuItem("Drop Zone を表示");
        _monitoringMenuItem = new Forms.ToolStripMenuItem("監視を一時停止") { Checked = false };
        var exitMenuItem = new Forms.ToolStripMenuItem("終了");

        openMenuItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        dryRunMenuItem.Click += (_, _) => DryRunRequested?.Invoke(this, EventArgs.Empty);
        dropZoneMenuItem.Click += (_, _) => DropZoneRequested?.Invoke(this, EventArgs.Empty);
        _monitoringMenuItem.Click += OnMonitoringClicked;
        exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(openMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(dryRunMenuItem);
        menu.Items.Add(dropZoneMenuItem);
        menu.Items.Add(_monitoringMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "File Organizer — フォルダを監視中",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? DryRunRequested;
    public event EventHandler? DropZoneRequested;
    public event EventHandler<MonitoringToggleRequestedEventArgs>? MonitoringToggleRequested;
    public event EventHandler? ExitRequested;

    public void SetMonitoringState(bool isMonitoring)
    {
        ThrowIfDisposed();
        _isMonitoring = isMonitoring;
        _monitoringMenuItem.Checked = !isMonitoring;
        _monitoringMenuItem.Text = isMonitoring ? "監視を一時停止" : "監視を再開";
        _notifyIcon.Text = isMonitoring
            ? "File Organizer — フォルダを監視中"
            : "File Organizer — 監視を一時停止中";
    }

    public void ShowNotification(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        ThrowIfDisposed();
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3500);
    }

    private void OnMonitoringClicked(object? sender, EventArgs e)
    {
        MonitoringToggleRequested?.Invoke(this, new MonitoringToggleRequestedEventArgs(!_isMonitoring));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}

public sealed class MonitoringToggleRequestedEventArgs : EventArgs
{
    public MonitoringToggleRequestedEventArgs(bool enabled) => Enabled = enabled;
    public bool Enabled { get; }
}
