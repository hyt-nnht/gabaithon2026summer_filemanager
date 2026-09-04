using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Extraction;

/// <summary>
/// DOCXパッケージ内のword/document.xmlから段落・表セルの文字列を直接取得する。
/// Wordアプリやマクロは実行せず、画像内文字のOCRも行わない。
/// </summary>
public sealed class DocxTextExtractor : IContentTextExtractor
{
    public const long DefaultMaxInputBytes = 20L * 1024 * 1024;
    public const long DefaultMaxDocumentXmlBytes = 20L * 1024 * 1024;

    private const string WordprocessingNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly long _maxInputBytes;
    private readonly long _maxDocumentXmlBytes;
    private readonly int _maxCharacters;

    public DocxTextExtractor(
        long maxInputBytes = DefaultMaxInputBytes,
        long maxDocumentXmlBytes = DefaultMaxDocumentXmlBytes,
        int maxCharacters = AnalyzeRequest.MaxOcrTextLength)
    {
        if (maxInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
        if (maxDocumentXmlBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDocumentXmlBytes));
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        _maxInputBytes = maxInputBytes;
        _maxDocumentXmlBytes = maxDocumentXmlBytes;
        _maxCharacters = maxCharacters;
    }

    public async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0 || info.Length > _maxInputBytes)
            {
                return null;
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? documentEntry = archive.GetEntry("word/document.xml");
            if (documentEntry is null || documentEntry.Length == 0 || documentEntry.Length > _maxDocumentXmlBytes)
            {
                return null;
            }

            await using Stream documentStream = documentEntry.Open();
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = _maxDocumentXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using XmlReader reader = XmlReader.Create(documentStream, settings);
            XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);

            XNamespace word = WordprocessingNamespace;
            var builder = new StringBuilder(Math.Min(_maxCharacters, 16_384));
            foreach (XElement paragraph in document.Descendants(word + "p"))
            {
                ct.ThrowIfCancellationRequested();
                var paragraphText = new StringBuilder();
                foreach (XElement element in paragraph.Descendants())
                {
                    if (element.Name == word + "t")
                    {
                        paragraphText.Append(element.Value);
                    }
                    else if (element.Name == word + "tab")
                    {
                        paragraphText.Append('\t');
                    }
                    else if (element.Name == word + "br" || element.Name == word + "cr")
                    {
                        paragraphText.Append('\n');
                    }
                }

                if (paragraphText.Length == 0)
                {
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                int remaining = _maxCharacters - builder.Length;
                if (remaining <= 0)
                {
                    break;
                }
                builder.Append(paragraphText.ToString(0, Math.Min(paragraphText.Length, remaining)));
                if (builder.Length >= _maxCharacters)
                {
                    break;
                }
            }

            string text = builder.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or NotSupportedException)
        {
            return null;
        }
    }
}
