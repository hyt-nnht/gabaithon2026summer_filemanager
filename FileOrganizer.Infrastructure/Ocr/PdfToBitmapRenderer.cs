using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileOrganizer.Infrastructure.Ocr;

/// <summary>
/// SPEC_v3.6.md §3.1「文字認識(OCR)」/ AI_IMPLEMENTATION_GUIDE.md が定める
/// 「Windows.Data.Pdf (200〜300DPI) + Windows.Media.Ocr」パイプラインのうち、
/// PDFの各ページをOCR可能なビットマップへラスタライズする前段処理を担う。
/// 生成した<see cref="SoftwareBitmap"/>はそのまま<c>Windows.Media.Ocr.OcrEngine.RecognizeAsync</c>へ渡せる。
/// </summary>
/// <remarks>
/// パスワード保護・破損・存在しない等、任意のファイルを対象とするOCR前処理として想定内の失敗は
/// 例外を投げず<c>null</c>／空一覧を返す。呼び出し元（WindowsMediaOcrService）はこれを受けて
/// ルールベース仕分けへgracefulフォールバックできる。想定外の例外（呼び出し引数の誤り等）は
/// 握りつぶさず伝播させる。
/// 戻り値の<see cref="SoftwareBitmap"/>はアンマネージリソースを保持するため、破棄責任は呼び出し元にある。
/// </remarks>
public sealed class PdfToBitmapRenderer
{
    /// <summary>既定のラスタライズ解像度（仕様書§3.1「200〜300DPI」の下限値）。呼び出し側で変更可能。</summary>
    public const double DefaultDpi = 200.0;

    // PDFページサイズの単位（1/72インチ＝ポイント）からピクセル数への換算基準
    private const double PdfPointsPerInch = 72.0;

    /// <summary>
    /// PDFの総ページ数を取得する。読み込みに失敗した場合（破損・パスワード保護・存在しない等）は0を返す。
    /// </summary>
    /// <param name="pdfFilePath">PDFファイルの絶対パス</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task<int> GetPageCountAsync(string pdfFilePath, CancellationToken ct = default)
    {
        PdfDocument? document = await TryLoadDocumentAsync(pdfFilePath, ct).ConfigureAwait(false);
        return document is null ? 0 : (int)document.PageCount;
    }

    /// <summary>
    /// 指定ページ（0始まり）を<paramref name="dpi"/>でラスタライズしたビットマップを返す。
    /// ページ番号が範囲外、またはPDF読み込みに失敗した場合はnullを返す（例外は投げない）。
    /// </summary>
    /// <param name="pdfFilePath">PDFファイルの絶対パス</param>
    /// <param name="pageIndex">0始まりのページ番号</param>
    /// <param name="dpi">ラスタライズ解像度（画素/インチ）。既定<see cref="DefaultDpi"/>（200DPI）。設定可能。</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task<SoftwareBitmap?> RenderPageAsync(
        string pdfFilePath,
        int pageIndex,
        double dpi = DefaultDpi,
        CancellationToken ct = default)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "pageIndexは0以上を指定してください。");
        }
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpiは正の値を指定してください。");
        }

        PdfDocument? document = await TryLoadDocumentAsync(pdfFilePath, ct).ConfigureAwait(false);
        if (document is null || pageIndex >= document.PageCount)
        {
            return null;
        }

        return await RenderPageCoreAsync(document, (uint)pageIndex, dpi, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// PDFの全ページを<paramref name="dpi"/>でラスタライズしたビットマップの一覧を返す。
    /// 読み込みに失敗した場合、またはいずれかのページのラスタライズに失敗した場合は空一覧を返す
    /// （例外は投げない。既に生成済みのビットマップは内部で破棄する）。
    /// </summary>
    /// <param name="pdfFilePath">PDFファイルの絶対パス</param>
    /// <param name="dpi">ラスタライズ解像度（画素/インチ）。既定<see cref="DefaultDpi"/>（200DPI）。設定可能。</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task<IReadOnlyList<SoftwareBitmap>> RenderAllPagesAsync(
        string pdfFilePath,
        double dpi = DefaultDpi,
        CancellationToken ct = default)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpiは正の値を指定してください。");
        }

        PdfDocument? document = await TryLoadDocumentAsync(pdfFilePath, ct).ConfigureAwait(false);
        if (document is null)
        {
            return Array.Empty<SoftwareBitmap>();
        }

        var pages = new List<SoftwareBitmap>((int)document.PageCount);
        try
        {
            for (uint i = 0; i < document.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                SoftwareBitmap? bitmap = await RenderPageCoreAsync(document, i, dpi, ct).ConfigureAwait(false);
                if (bitmap is null)
                {
                    // 1ページでもラスタライズに失敗した場合、部分結果はOCR前処理として不完全なため
                    // 破棄した上で空一覧を返し、呼び出し元のgracefulフォールバックに委ねる。
                    DisposeAll(pages);
                    return Array.Empty<SoftwareBitmap>();
                }

                pages.Add(bitmap);
            }
        }
        catch (OperationCanceledException)
        {
            DisposeAll(pages);
            throw;
        }

        return pages;
    }

    private static async Task<SoftwareBitmap?> RenderPageCoreAsync(
        PdfDocument document, uint pageIndex, double dpi, CancellationToken ct)
    {
        try
        {
            using PdfPage page = document.GetPage(pageIndex);

            double scale = dpi / PdfPointsPerInch;
            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
            };

            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, renderOptions).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            return await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedPdfFailure(ex))
        {
            return null;
        }
    }

    private static async Task<PdfDocument?> TryLoadDocumentAsync(string pdfFilePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfFilePath);

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(pdfFilePath).AsTask(ct).ConfigureAwait(false);
            return await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedPdfFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// 破損・パスワード保護・存在しない・アクセス不可等、任意のファイルを対象とするOCR前処理として
    /// 想定内に扱うべき失敗を判定する。想定外の例外（呼び出し側の誤り等）はここで握りつぶさず伝播させる。
    /// </summary>
    private static bool IsExpectedPdfFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or COMException;

    private static void DisposeAll(IEnumerable<SoftwareBitmap> bitmaps)
    {
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }
}
