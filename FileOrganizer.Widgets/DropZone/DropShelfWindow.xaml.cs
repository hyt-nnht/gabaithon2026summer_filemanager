using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Wpf = System.Windows;
using WpfMedia = System.Windows.Media;

namespace FileOrganizer.Widgets.DropZone;

/// <summary>
/// ドロップされたパスを表示し、承認要求を実行ホストへ通知するだけのView。
/// ルール評価・OCR・移動は行わない。ProductionホストがFilesSubmittedをDryRunSimulatorへ接続する。
/// </summary>
public partial class DropShelfWindow : Window
{
    private static readonly WpfMedia.Brush IdleBackground =
        new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(248, 249, 255));

    private static readonly WpfMedia.Brush ActiveBackground =
        new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(235, 237, 255));

    public DropShelfWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => PositionNearWorkAreaEdge();
    }

    public ObservableCollection<DroppedFileItem> DroppedFiles { get; } = new();
    public event EventHandler<DroppedFilesSubmittedEventArgs>? FilesSubmitted;

    private void OnDragEnter(object sender, Wpf.DragEventArgs e)
    {
        bool hasFiles = e.Data.GetDataPresent(Wpf.DataFormats.FileDrop);
        e.Effects = hasFiles ? Wpf.DragDropEffects.Copy : Wpf.DragDropEffects.None;
        DropSurface.Background = hasFiles ? ActiveBackground : IdleBackground;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, Wpf.DragEventArgs e)
    {
        DropSurface.Background = IdleBackground;
    }

    private void OnDrop(object sender, Wpf.DragEventArgs e)
    {
        DropSurface.Background = IdleBackground;
        if (e.Data.GetData(Wpf.DataFormats.FileDrop) is not string[] paths)
            return;

        foreach (string path in paths)
        {
            if (DroppedFiles.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            DroppedFiles.Add(new DroppedFileItem(path));
        }

        UpdateStateVisibility();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        DroppedFiles.Clear();
        UpdateStateVisibility();
    }

    private void OnOrganizeClicked(object sender, RoutedEventArgs e)
    {
        if (DroppedFiles.Count == 0)
            return;

        FilesSubmitted?.Invoke(this, new DroppedFilesSubmittedEventArgs(DroppedFiles.Select(item => item.FullPath).ToArray()));
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void UpdateStateVisibility()
    {
        bool hasItems = DroppedFiles.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        DroppedFilesList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PositionNearWorkAreaEdge()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 18;
        Top = area.Bottom - ActualHeight - 18;
    }
}

public sealed class DroppedFileItem
{
    public DroppedFileItem(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        DirectoryLabel = Path.GetDirectoryName(fullPath) ?? string.Empty;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string DirectoryLabel { get; }
}

public sealed class DroppedFilesSubmittedEventArgs : EventArgs
{
    public DroppedFilesSubmittedEventArgs(IReadOnlyList<string> paths) => Paths = paths;
    public IReadOnlyList<string> Paths { get; }
}
