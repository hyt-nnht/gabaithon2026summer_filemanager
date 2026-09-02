using System;
using System.IO;
using System.Security.Cryptography;

namespace FileOrganizer.Core.Utils;

/// <summary>
/// AI_IMPLEMENTATION_GUIDE.md §5.2準拠の軽量ハッシュユーティリティ。
/// 仕様書§3.3「軽量ハッシュ仕様」・§7.1（大容量ファイルでのI/Oスパイク回避）を満たすため、
/// 100MB未満は全バイトSHA256、100MB以上は「サイズ+更新日時Ticks（メタデータ）＋先頭2MB＋末尾2MB」の
/// 結合ハッシュで代替する。
/// </summary>
public static class HashHelper
{
    private const long LargeFileThreshold = 100 * 1024 * 1024; // 100MB
    private const int ChunkSize = 2 * 1024 * 1024;             // 2MB

    public static string ComputeLightweightHash(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return string.Empty;

        // 100MB未満は全バイト計算
        if (fileInfo.Length < LargeFileThreshold)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        return ComputeChunkedHash(filePath);
    }

    /// <summary>
    /// 「メタデータ＋先頭2MB＋末尾2MB」の結合ハッシュを、<see cref="LargeFileThreshold"/>（100MB）による
    /// 経路判定を経由せずに直接計算する。<see cref="ComputeLightweightHash"/>が100MB以上のファイルに対して
    /// 使う実装と共通であり、先頭ブロックと末尾ブロックが重複しうる境界（ファイルサイズが
    /// <see cref="ChunkSize"/>の2倍＝4MB未満）でも例外なく計算できることを単体テストから直接検証するために
    /// internal公開している。
    /// </summary>
    internal static string ComputeChunkedHash(string filePath)
    {
        var fileInfo = new FileInfo(filePath);

        // 100MB以上の大容量ファイル: メタデータ + 先頭2MB + 末尾2MB の結合ハッシュ
        using var fs = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();

        // 1. メタデータプレフィックス（サイズ + 更新日時Ticks）
        string metaPrefix = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}_";
        byte[] metaBytes = System.Text.Encoding.UTF8.GetBytes(metaPrefix);
        sha256.TransformBlock(metaBytes, 0, metaBytes.Length, null, 0);

        // 2. 先頭ブロック読み取り
        byte[] headBuffer = new byte[ChunkSize];
        int headBytesRead = ReadBlockFully(fs, headBuffer, 0, ChunkSize);
        sha256.TransformBlock(headBuffer, 0, headBytesRead, null, 0);

        // 3. 末尾ブロック読み取り（先頭と重複しない位置へシーク）
        long tailOffset = Math.Max((long)headBytesRead, fs.Length - ChunkSize);
        int tailBytesToRead = (int)(fs.Length - tailOffset);

        if (tailBytesToRead > 0)
        {
            fs.Seek(tailOffset, SeekOrigin.Begin);
            byte[] tailBuffer = new byte[tailBytesToRead];
            int tailBytesRead = ReadBlockFully(fs, tailBuffer, 0, tailBytesToRead);
            sha256.TransformFinalBlock(tailBuffer, 0, tailBytesRead);
        }
        else
        {
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }

        return Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>());
    }

    /// <summary>
    /// 部分読み取りに対応し、要求バイト数またはEOFまで確実にストリームを読み切る補助メソッド
    /// </summary>
    private static int ReadBlockFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) break; // EOF
            totalRead += read;
        }
        return totalRead;
    }
}
