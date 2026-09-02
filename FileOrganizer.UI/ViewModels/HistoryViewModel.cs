using System.Collections.ObjectModel;
using System.IO;
using FileOrganizer.Shared.Models;
using FileOrganizer.UI.Mvvm;
using FileOrganizer.UI.Services;

namespace FileOrganizer.UI.ViewModels;

public sealed class HistoryViewModel : ObservableObject
{
    private readonly IFrontendBackendGateway _gateway;
    private readonly Action<string> _showMessage;
    private readonly List<HistoryItemViewModel> _allItems = new();
    private string _searchText = string.Empty;
    private string _selectedFilter = "すべて";

    public HistoryViewModel(IFrontendBackendGateway gateway, Action<string> showMessage)
    {
        _gateway = gateway;
        _showMessage = showMessage;
        Filters = new[] { "すべて", "完了", "失敗", "復元済み" };
        UndoCommand = new AsyncRelayCommand(UndoAsync, parameter => parameter is HistoryItemViewModel { CanUndo: true });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();
    public IReadOnlyList<string> Filters { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
                ApplyFilter();
        }
    }

    public bool HasItems => Items.Count > 0;

    public void Load(IEnumerable<HistoryRecord> records)
    {
        _allItems.Clear();
        _allItems.AddRange(records.Select(record => new HistoryItemViewModel(record)));
        ApplyFilter();
    }

    public async Task RefreshAsync()
    {
        try
        {
            Load(await _gateway.LoadHistoryAsync());
        }
        catch (Exception ex)
        {
            _showMessage($"履歴を更新できませんでした: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<HistoryItemViewModel> query = _allItems;
        query = SelectedFilter switch
        {
            "完了" => query.Where(item => item.State == OperationState.Completed),
            "失敗" => query.Where(item => item.State is OperationState.Failed or OperationState.UndoFailed),
            "復元済み" => query.Where(item => item.State == OperationState.Undone),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(item =>
                item.SourcePath.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                item.DestinationPath.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        }

        Items.Clear();
        foreach (var item in query)
            Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
    }

    private async Task UndoAsync(object? parameter)
    {
        if (parameter is not HistoryItemViewModel item)
            return;

        item.IsBusy = true;
        try
        {
            // Production GatewayではIUndoManagerがハッシュとパス衝突を再検証する。
            var result = await _gateway.UndoAsync(item.Id);
            _showMessage(result.Message ?? "復元処理が完了しました。");
            if (result.Outcome == UndoOutcome.Success)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            _showMessage($"元に戻せませんでした: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }
}

public sealed class HistoryItemViewModel : ObservableObject
{
    private OperationState _state;
    private bool _isBusy;

    public HistoryItemViewModel(HistoryRecord record)
    {
        Id = record.Id;
        OperationId = record.OperationId;
        OperationType = record.OpType;
        SourcePath = record.SourcePath;
        DestinationPath = record.DestinationPath ?? "—";
        _state = record.State;
        ErrorMessage = record.ErrorMessage ?? string.Empty;
        CreatedAtLocal = record.CreatedAtUtc.ToLocalTime();
        FileSizeLabel = FormatFileSize(record.FileSizeBytes);
    }

    public long Id { get; }
    public string OperationId { get; }
    public OperationType OperationType { get; }
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public string ErrorMessage { get; }
    public DateTime CreatedAtLocal { get; }
    public string FileSizeLabel { get; }

    public OperationState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(StateBrushKey));
                OnPropertyChanged(nameof(CanUndo));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanUndo));
        }
    }

    public bool CanUndo => !IsBusy && State == OperationState.Completed && OperationType != OperationType.Recycle;
    public string OperationLabel => OperationType switch
    {
        OperationType.Move => "移動",
        OperationType.Rename => "名前変更",
        OperationType.Copy => "コピー",
        OperationType.Recycle => "ゴミ箱",
        _ => OperationType.ToString()
    };

    public string StateLabel => State switch
    {
        OperationState.Planned => "予定",
        OperationState.Executing => "処理中",
        OperationState.Completed => "完了",
        OperationState.Failed => "失敗",
        OperationState.Undoing => "復元中",
        OperationState.Undone => "復元済み",
        OperationState.UndoFailed => "復元失敗",
        _ => State.ToString()
    };

    public string StateBrushKey => State switch
    {
        OperationState.Completed => "success",
        OperationState.Failed or OperationState.UndoFailed => "danger",
        OperationState.Undone => "neutral",
        _ => "warning"
    };

    public string TimeLabel => CreatedAtLocal.Date == DateTime.Today
        ? $"今日 {CreatedAtLocal:HH:mm}"
        : CreatedAtLocal.ToString("M月d日 HH:mm");

    public string FileName => Path.GetFileName(SourcePath);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024d / 1024d:0.0} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }
}
