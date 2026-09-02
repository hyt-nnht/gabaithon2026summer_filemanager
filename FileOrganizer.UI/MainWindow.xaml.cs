using System.Windows;
using FileOrganizer.UI.ViewModels;
using FileOrganizer.UI.Views;

namespace FileOrganizer.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // WindowとDashboardは同じライフタイム。閉じる際に購読解除して将来のトレイ常駐にも備える。
        _viewModel.Dashboard.DryRunRequested += OnDryRunRequested;
        Closed += (_, _) => _viewModel.Dashboard.DryRunRequested -= OnDryRunRequested;
    }

    private void OnDryRunRequested(object? sender, EventArgs e)
    {
        var dryRunViewModel = _viewModel.CreateDryRunViewModel(_viewModel.Dashboard.PrimaryWatchFolder);
        ShowDryRun(dryRunViewModel);
    }

    public void ShowDryRunForFiles(IReadOnlyList<string> filePaths)
        => ShowDryRun(_viewModel.CreateDryRunViewModel(filePaths));

    private void ShowDryRun(DryRunViewModel dryRunViewModel)
    {
        var dialog = new DryRunWindow(dryRunViewModel)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            _ = RefreshAfterDryRunAsync();
    }

    private async Task RefreshAfterDryRunAsync()
    {
        try { await _viewModel.RefreshRuntimeAsync(); }
        catch (Exception ex) { _viewModel.ShowMessage($"実行後の画面更新に失敗しました: {ex.Message}"); }
    }
}
