using System.Text;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Extraction;

/// <summary>UTF-8またはBOM付きUTF-16のTXT本文を、OCRを介さず直接読み込む。</summary>
public sealed class PlainTextExtractor : IContentTextExtractor
{
    public const long DefaultMaxInputBytes = 20L * 1024 * 1024;

    private readonly long _maxInputBytes;
    private readonly int _maxCharacters;

    public PlainTextExtractor(
        long maxInputBytes = DefaultMaxInputBytes,
        int maxCharacters = AnalyzeRequest.MaxOcrTextLength)
    {
        if (maxInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        _maxInputBytes = maxInputBytes;
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
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);

            var builder = new StringBuilder(Math.Min(_maxCharacters, 16_384));
            var buffer = new char[4096];
            while (builder.Length <= _maxCharacters)
            {
                int remaining = _maxCharacters + 1 - builder.Length;
                int read = await reader.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                builder.Append(buffer, 0, read);
            }

            string text = builder.Length > _maxCharacters
                ? builder.ToString(0, _maxCharacters)
                : builder.ToString();
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0'))
            {
                return null;
            }
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or NotSupportedException)
        {
            return null;
        }
    }
}
