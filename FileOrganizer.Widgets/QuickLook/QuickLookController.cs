using System.Windows.Threading;

namespace FileOrganizer.Widgets.QuickLook;

/// <summary>キーフック、誤爆防止判定、プレビュー生成、Window表示を接続する。</summary>
public sealed class QuickLookController : IDisposable
{
    private readonly Func<bool> _isEnabled;
    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _keyboardHook;
    private readonly ExplorerSelectionProvider _selectionProvider = new();
    private readonly QuickLookPreviewProvider _previewProvider = new();
    private QuickLookWindow? _window;
    private CancellationTokenSource? _previewCancellation;
    private bool _disposed;

    public QuickLookController(Func<bool> isEnabled, Dispatcher dispatcher)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _keyboardHook = new KeyboardHook();
        _keyboardHook.SpacePressed += OnSpacePressed;
    }

    private void OnSpacePressed(object? sender, EventArgs e)
    {
        if (!_isEnabled()) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(HandleSpaceAsync));
    }

    private async void HandleSpaceAsync()
    {
        if (_disposed) return;
        if (_window?.IsVisible == true)
        {
            _window.Hide();
            return;
        }

        QuickLookActivationDecision decision = QuickLookActivationPolicy.Evaluate(_selectionProvider.Capture());
        if (!decision.ShouldOpen || decision.SelectedFilePath is null) return;

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        try
        {
            QuickLookPresentation presentation = await _previewProvider
                .CreateAsync(decision.SelectedFilePath, _previewCancellation.Token);
            if (_disposed) return;
            _window ??= new QuickLookWindow();
            _window.ShowPreview(presentation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Explorer上の選択は表示までに変化しうる。失敗時は何も開かず次の操作を待つ。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keyboardHook.SpacePressed -= OnSpacePressed;
        _keyboardHook.Dispose();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _window?.Close();
    }
}
