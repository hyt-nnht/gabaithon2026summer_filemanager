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
    private int _isHandling;
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

        // PDFレンダリング等の非同期処理中にSpaceの多重発火（キーリピートや、await中のメッセージポンプ
        // 再入）が起きても同時実行しない。取りこぼした押下は次のSpaceで拾われるため実害はない。
        if (Interlocked.CompareExchange(ref _isHandling, 1, 0) != 0) return;
        try
        {
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
                // Windowが表示更新の途中で例外を投げた場合、以後ずっとそのWindowを再利用して
                // 壊れた状態を引きずらないよう、破棄して次回作り直す（自己修復）。
                try { _window?.Close(); } catch { /* 破棄自体の失敗は無視し、参照だけ捨てる */ }
                _window = null;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isHandling, 0);
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
