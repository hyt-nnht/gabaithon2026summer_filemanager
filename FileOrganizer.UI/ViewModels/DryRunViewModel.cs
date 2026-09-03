using System.Collections.ObjectModel;
using System.IO;
using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Models;
using FileOrganizer.UI.Mvvm;
using FileOrganizer.UI.Services;

namespace FileOrganizer.UI.ViewModels;

public sealed class DryRunViewModel : ObservableObject, IDisposable
{
    private readonly IFrontendBackendGateway _gateway;
    private readonly IReadOnlyList<string>? _filePaths;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _isLoading;
    private string _statusMessage = "必要に応じてOCR・AI分類を実行しています…";

    public DryRunViewModel(IFrontendBackendGateway gateway, string folderPath)
    {
        _gateway = gateway;
        FolderPath = folderPath;
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => Items.Any(item => item.IsSelected));
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public DryRunViewModel(IFrontendBackendGateway gateway, IReadOnlyList<string> filePaths)
        : this(gateway, $"ドロップしたファイル（{filePaths.Count}件）")
    {
        _filePaths = filePaths;
    }

    public event EventHandler<bool>? CloseRequested;
    public ObservableCollection<DryRunItemViewModel> Items { get; } = new();
    public string FolderPath { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int SelectedCount => Items.Count(item => item.IsSelected);
    public bool AreAllSelected
    {
        get
        {
            DryRunItemViewModel[] selectable = Items.Where(item => !item.RequiresConfirmation).ToArray();
            return selectable.Length > 0 && selectable.All(item => item.IsSelected);
        }
        set => ToggleAll(value);
    }
    public string ExecuteLabel => _gateway.IsBackendConnected ? $"選択した {SelectedCount} 件を実行" : $"{SelectedCount} 件を承認（UI確認）";

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            var plans = _filePaths is null
                ? await _gateway.PreviewCleanupAsync(FolderPath, _lifetimeCancellation.Token)
                : await _gateway.PreviewFilesAsync(_filePaths, _lifetimeCancellation.Token);
            Items.Clear();
            foreach (var plan in plans)
            {
                var item = new DryRunItemViewModel(plan);
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(DryRunItemViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(SelectedCount));
                        OnPropertyChanged(nameof(ExecuteLabel));
                        OnPropertyChanged(nameof(AreAllSelected));
                        ExecuteCommand.NotifyCanExecuteChanged();
                    }
                };
                Items.Add(item);
            }

            StatusMessage = plans.Count == 0
                ? "整理対象のファイルは見つかりませんでした。"
                : $"{plans.Count} 件の変更候補があります。実行直前にパスと衝突を再検証します。";
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(ExecuteLabel));
            OnPropertyChanged(nameof(AreAllSelected));
            ExecuteCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // ダイアログが閉じられた。画面更新やエラー通知は不要。
        }
        catch (Exception ex)
        {
            StatusMessage = $"プレビューを作成できませんでした: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ToggleAll(bool isSelected)
    {
        foreach (var item in Items)
            item.IsSelected = isSelected;
    }

    private async Task ExecuteAsync()
    {
        try
        {
            var approved = Items.Where(item => item.IsSelected).Select(item => item.Source).ToList();
            var result = await _gateway.ExecuteCleanupAsync(approved, _lifetimeCancellation.Token);
            StatusMessage = result.Message;
            if (result.Success)
                CloseRequested?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "処理をキャンセルしました。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"承認した内容を実行できませんでした: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}

public sealed class DryRunItemViewModel : ObservableObject
{
    private bool _isSelected;

    public DryRunItemViewModel(DryRunPreviewItem source)
    {
        Source = source;
        _isSelected = !source.RequiresConfirmation;
    }

    public DryRunPreviewItem Source { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && RequiresConfirmation) return;
            SetProperty(ref _isSelected, value);
        }
    }
    public string SourcePath => Source.SourcePath;
    public string DestinationPath => Source.Actions.LastOrDefault()?.OperationType == OperationType.Recycle
        ? "Windowsのゴミ箱"
        : Source.DestinationPath ?? "実行時に決定";
    public string SourceFileName => Path.GetFileName(Source.SourcePath);
    public string RuleName => Source.RuleName;
    public string Note => Source.Note;
    public bool RequiresConfirmation => Source.RequiresConfirmation;
    public string OperationLabel => string.Join(" → ", Source.Actions.Select(action => action.OperationType switch
    {
        OperationType.Move => "移動",
        OperationType.Rename => "名前変更",
        OperationType.Copy => "コピー",
        OperationType.Recycle => "ゴミ箱",
        _ => action.OperationType.ToString()
    }));
}
