using System.Windows;
using FileOrganizer.UI.ViewModels;

namespace FileOrganizer.UI.Views;

public partial class DryRunWindow : Window
{
    private readonly DryRunViewModel _viewModel;

    public DryRunWindow(DryRunViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync();
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
        Closed -= OnClosed;
    }
}
