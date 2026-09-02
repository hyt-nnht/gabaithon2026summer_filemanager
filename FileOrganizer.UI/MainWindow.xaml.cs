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
        var dialog = new DryRunWindow(dryRunViewModel)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }
}
