using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FileOrganizer.Core.Extraction;
using FileOrganizer.Shared.Contracts;

namespace FileOrganizer.Core.Tests.Extraction;

public sealed class ContentTextExtractionTests : IDisposable
{
    private static readonly XNamespace Word =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(),
        "FileOrganizerTests",
        "ContentTextExtraction",
        Guid.NewGuid().ToString("N"));

    public ContentTextExtractionTests() => Directory.CreateDirectory(_workDir);

    [Fact]
    public async Task PlainTextExtractor_UTF8本文を直接読み込む()
    {
        string path = Path.Combine(_workDir, "invoice.txt");
        await File.WriteAllTextAsync(path, "請求書\n発行元: サンプル株式会社", new UTF8Encoding(false));

        string? result = await new PlainTextExtractor().ExtractTextAsync(path);

        Assert.Equal("請求書\n発行元: サンプル株式会社", result);
    }

    [Fact]
    public async Task PlainTextExtractor_上限を超える本文は上限文字数で切り詰める()
    {
        string path = Path.Combine(_workDir, "long.txt");
        await File.WriteAllTextAsync(path, "1234567890", new UTF8Encoding(false));

        string? result = await new PlainTextExtractor(maxCharacters: 5).ExtractTextAsync(path);

        Assert.Equal("12345", result);
    }

    [Fact]
    public async Task DocxTextExtractor_段落と表セルをWordなしで直接読み込む()
    {
        string path = CreateDocx(
            "delivery.docx",
            Paragraph("納品書"),
            new XElement(Word + "tbl",
                new XElement(Word + "tr",
                    new XElement(Word + "tc", Paragraph("納品番号: DN-001")),
                    new XElement(Word + "tc", Paragraph("納品日: 2026-09-04")))));

        string? result = await new DocxTextExtractor().ExtractTextAsync(path);

        Assert.NotNull(result);
        Assert.Contains("納品書", result);
        Assert.Contains("納品番号: DN-001", result);
        Assert.Contains("納品日: 2026-09-04", result);
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("docx")]
    public async Task Router_TXT_DOCXではOCR言語確認もOCR抽出も呼ばない(string extension)
    {
        string path = extension == "txt"
            ? CreateTextFile("direct.txt", "請求書")
            : CreateDocx("direct.docx", Paragraph("請求書"));
        var ocr = new RecordingOcrService { LanguagePackAvailable = false };
        var router = new ContentTextExtractionRouter(ocr);

        string? result = await router.ExtractTextAsync(path);

        Assert.Contains("請求書", result);
        Assert.Equal(0, ocr.LanguageCheckCount);
        Assert.Equal(0, ocr.ExtractCount);
    }

    [Fact]
    public async Task Router_PDF画像経路では既存OCRへ委譲する()
    {
        string path = Path.Combine(_workDir, "sample.pdf");
        await File.WriteAllBytesAsync(path, [0x25, 0x50, 0x44, 0x46]);
        var ocr = new RecordingOcrService
        {
            LanguagePackAvailable = true,
            TextToReturn = "OCRで取得した本文",
        };
        var router = new ContentTextExtractionRouter(ocr);

        string? result = await router.ExtractTextAsync(path);

        Assert.Equal("OCRで取得した本文", result);
        Assert.Equal(1, ocr.LanguageCheckCount);
        Assert.Equal(1, ocr.ExtractCount);
    }

    private string CreateTextFile(string fileName, string content)
    {
        string path = Path.Combine(_workDir, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string CreateDocx(string fileName, params XElement[] bodyElements)
    {
        string path = Path.Combine(_workDir, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("word/document.xml");
        using Stream stream = entry.Open();
        new XDocument(new XElement(Word + "document", new XElement(Word + "body", bodyElements))).Save(stream);
        return path;
    }

    private static XElement Paragraph(string text) =>
        new(Word + "p", new XElement(Word + "r", new XElement(Word + "t", text)));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch
        {
            // テスト一時ファイルの後始末失敗は無視する。
        }
    }

    private sealed class RecordingOcrService : IOcrService
    {
        public bool LanguagePackAvailable { get; init; } = true;
        public string? TextToReturn { get; init; }
        public int LanguageCheckCount { get; private set; }
        public int ExtractCount { get; private set; }

        public Task<bool> IsLanguagePackAvailableAsync()
        {
            LanguageCheckCount++;
            return Task.FromResult(LanguagePackAvailable);
        }

        public Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
        {
            ExtractCount++;
            return Task.FromResult(TextToReturn);
        }
    }
}
