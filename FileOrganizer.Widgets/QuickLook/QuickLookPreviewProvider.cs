using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileOrganizer.Widgets.QuickLook;

public sealed class QuickLookPreviewProvider
{
    private const int MaxPreviewCharacters = 64 * 1024;

    // OCR前処理（200〜300DPI）とは異なり、QuickLookはプレビュー表示の可読性が目的のため軽量なDPIで十分。
    private const double PdfPreviewDpi = 150.0;
    private const double PdfPointsPerInch = 72.0;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".log", ".cs", ".py", ".js", ".ts", ".html", ".css"
    };

    public async Task<QuickLookPresentation> CreateAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("プレビュー対象が見つかりません。", path);

        string extension = info.Extension;

        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            // Windows.Data.Pdf(WinRT)呼び出しをUIスレッド（グローバルキーフックのメッセージポンプと同じ
            // STAスレッド）から切り離す。同スレッド上でのWinRT非同期await中に発生し得る
            // メッセージポンプの再入を避け、次のQuick Look起動に影響を残さないようにする。
            return await Task.Run(() => CreatePdfPresentationAsync(info, ct), ct).ConfigureAwait(false);
        }

        string preview;
        string kind;
        if (TextExtensions.Contains(extension))
        {
            kind = "テキスト";
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            // 上限より1文字だけ多く非同期で読み、同期的なEndOfStream参照をせずに
            // 「末尾が省略されたか」を判定する（CA2024対策）。
            char[] buffer = new char[MaxPreviewCharacters + 1];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            int previewLength = Math.Min(read, MaxPreviewCharacters);
            preview = new string(buffer, 0, previewLength);
            if (read > MaxPreviewCharacters) preview += "\n\n…（先頭64K文字まで表示）";
        }
        else
        {
            kind = string.IsNullOrWhiteSpace(extension) ? "ファイル" : $"{extension.TrimStart('.').ToUpperInvariant()} ファイル";
            preview = "この形式は内容のテキスト表示に対応していません。\nアプリで開く前に、名前・場所・サイズ・更新日時を確認できます。";
        }

        return new QuickLookPresentation(
            info.Name,
            info.FullName,
            string.IsNullOrWhiteSpace(extension) ? "F" : extension.TrimStart('.').ToUpperInvariant()[..Math.Min(3, extension.TrimStart('.').Length)],
            kind,
            $"{FormatSize(info.Length)}  •  更新 {info.LastWriteTime:g}",
            preview);
    }

    /// <summary>
    /// PDFの1ページ目をラスタライズして画像プレビューを作る。パスワード保護・破損等の
    /// 想定内の失敗時は例外を投げず、名前・場所・サイズだけを示すフォールバック表示にする。
    /// </summary>
    private static async Task<QuickLookPresentation> CreatePdfPresentationAsync(FileInfo info, CancellationToken ct)
    {
        string detail = $"{FormatSize(info.Length)}  •  更新 {info.LastWriteTime:g}";
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(info.FullName).AsTask(ct).ConfigureAwait(false);
            PdfDocument document = await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
            if (document.PageCount == 0)
            {
                return CreatePdfFallback(info, detail, "このPDFにはページがありません。");
            }

            using PdfPage page = document.GetPage(0);
            double scale = PdfPreviewDpi / PdfPointsPerInch;
            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
            };

            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, renderOptions).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var bitmap = new BitmapImage();
            using (Stream netStream = stream.AsStreamForRead())
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = netStream;
                bitmap.EndInit();
            }
            bitmap.Freeze(); // バックグラウンドスレッドで生成するためUIスレッド外へ安全に受け渡す

            string pageSuffix = document.PageCount > 1
                ? $"　•　全{document.PageCount}ページ（1ページ目を表示）"
                : string.Empty;

            return new QuickLookPresentation(
                info.Name,
                info.FullName,
                "PDF",
                "PDFドキュメント",
                detail + pageSuffix,
                string.Empty,
                bitmap);
        }
        catch (Exception ex) when (IsExpectedPdfFailure(ex))
        {
            return CreatePdfFallback(info, detail, "PDFの1ページ目を読み込めませんでした（パスワード保護・破損の可能性があります）。");
        }
    }

    private static QuickLookPresentation CreatePdfFallback(FileInfo info, string detail, string message)
        => new(info.Name, info.FullName, "PDF", "PDFドキュメント", detail, message);

    /// <summary>
    /// パスワード保護・破損・存在しない・アクセス不可等、任意のPDFを対象とするプレビューとして
    /// 想定内に扱うべき失敗を判定する。想定外の例外は握りつぶさず伝播させる。
    /// </summary>
    private static bool IsExpectedPdfFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or COMException or FileNotFoundException;

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB"
         : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB"
         : $"{bytes} B";
}
