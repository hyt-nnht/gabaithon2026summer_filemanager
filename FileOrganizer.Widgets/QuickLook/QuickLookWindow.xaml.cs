using System.Windows;
using System.Windows.Media;
using WpfInput = System.Windows.Input;

namespace FileOrganizer.Widgets.QuickLook;

/// <summary>
/// PreviewProviderが生成した表示用データだけを描画する。ファイル読取やキーフックはここでは行わない。
/// ProductionではKeyboardHookの全ガード通過後にPreviewProviderを呼び、その結果をShowPreviewへ渡す。
/// </summary>
public partial class QuickLookWindow : Window
{
    public QuickLookWindow()
    {
        InitializeComponent();
        DataContext = QuickLookPresentation.Empty;
    }

    public void ShowPreview(QuickLookPresentation presentation)
    {
        DataContext = presentation;

        // PDFはページ画像、それ以外（テキスト等）は文字列で表示するため、
        // どちらか一方だけを可視化する（ImageSourceの有無で判定）。
        bool hasImage = presentation.PreviewImage is not null;
        PreviewImageControl.Source = presentation.PreviewImage;
        ImagePreviewScroll.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        TextPreviewScroll.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;

        // 前回の表示がスクロールされたままだと、次のファイルを開いた瞬間に先頭が見えない。
        ImagePreviewScroll.ScrollToHome();
        TextPreviewScroll.ScrollToHome();

        Show();
        Activate();
        Focus();
    }

    private void OnKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (e.Key is WpfInput.Key.Space or WpfInput.Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();
}

public sealed record QuickLookPresentation(
    string FileName,
    string FilePath,
    string KindGlyph,
    string KindLabel,
    string DetailLabel,
    string PreviewText,
    ImageSource? PreviewImage = null)
{
    public static QuickLookPresentation Empty { get; } = new(
        "プレビューするファイルがありません",
        string.Empty,
        "F",
        "未選択",
        string.Empty,
        "エクスプローラーでファイルを選択し、Spaceキーを押すとここに内容を表示します。");
}
