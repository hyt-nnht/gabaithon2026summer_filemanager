using System.Security.Cryptography;
using FileOrganizer.Core.Utils;

namespace FileOrganizer.Core.Tests.Utils;

/// <summary>
/// 100MB以上のテストファイルを1個だけ用意し、<see cref="HashHelperTests"/>内の全テストで使い回すための
/// クラスフィクスチャ。中身は先頭2MB・末尾2MBのみ乱数で埋め、中間はゼロ埋め（<see cref="FileStream.SetLength"/>）
/// とすることで、実ファイルサイズを100MB以上に保ちながら生成コストを抑える
/// （HashHelperはメタデータ+先頭2MB+末尾2MBしか読まないため、中間の内容はハッシュ結果に影響しない）。
/// </summary>
public sealed class LargeFileFixture : IDisposable
{
    private const int ChunkSize = 2 * 1024 * 1024;

    public string WorkDir { get; }

    /// <summary>ちょうど100MB（閾値と同値、`&lt;`判定のためチャンク経路に入る境界ケース）。</summary>
    public string AtThresholdFilePath { get; }
    public long AtThresholdFileSize { get; } = 100L * 1024 * 1024;

    public LargeFileFixture()
    {
        WorkDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "HashHelperLarge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkDir);

        AtThresholdFilePath = Path.Combine(WorkDir, "at-threshold-100mb.bin");
        CreateFileWithRandomEdges(AtThresholdFilePath, AtThresholdFileSize, seed: 12345);
    }

    private static void CreateFileWithRandomEdges(string path, long totalSize, int seed)
    {
        var rng = new Random(seed);
        byte[] head = new byte[ChunkSize];
        byte[] tail = new byte[ChunkSize];
        rng.NextBytes(head);
        rng.NextBytes(tail);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(totalSize);
        fs.Position = 0;
        fs.Write(head, 0, head.Length);
        fs.Position = totalSize - tail.Length;
        fs.Write(tail, 0, tail.Length);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WorkDir))
            {
                Directory.Delete(WorkDir, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの後始末失敗は無視。
        }
    }
}

/// <summary>
/// 仕様書§3.3「軽量ハッシュ仕様」・§7.1（大容量ファイルでのI/Oスパイク回避）の受け入れ基準を検証する。
/// 対象: <see cref="HashHelper.ComputeLightweightHash"/>（AI_IMPLEMENTATION_GUIDE.md §5.2準拠実装）。
/// </summary>
public class HashHelperTests : IClassFixture<LargeFileFixture>, IDisposable
{
    private readonly LargeFileFixture _large;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "HashHelperSmall", Guid.NewGuid().ToString("N"));

    public HashHelperTests(LargeFileFixture large)
    {
        _large = large;
        Directory.CreateDirectory(_workDir);
    }

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
            // 一時フォルダの後始末失敗は無視。
        }
    }

    private string CreateFile(string fileName, byte[] content)
    {
        string path = Path.Combine(_workDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] CreateRandomBytes(int size, int seed)
    {
        var rng = new Random(seed);
        byte[] buffer = new byte[size];
        rng.NextBytes(buffer);
        return buffer;
    }

    /// <summary>
    /// HashHelper本体とは独立した経路（全バイトをメモリへ読み込んでからスライス）で
    /// 「メタデータ＋先頭2MB＋末尾2MB」の結合ハッシュを再計算する参照実装。
    /// 仕様書§3.3のアルゴリズムどおりに先頭/末尾チャンクの重複回避（<c>Math.Max</c>相当）も再現し、
    /// HashHelperの結果が仕様の定義どおりであることをクロスチェックする。
    /// </summary>
    private static string ComputeReferenceChunkedHash(string filePath)
    {
        const int chunkSize = 2 * 1024 * 1024;
        var fileInfo = new FileInfo(filePath);
        long length = fileInfo.Length;
        byte[] all = File.ReadAllBytes(filePath);

        string metaPrefix = $"{length}_{fileInfo.LastWriteTimeUtc.Ticks}_";
        byte[] metaBytes = System.Text.Encoding.UTF8.GetBytes(metaPrefix);

        int headLen = (int)Math.Min(length, chunkSize);
        long tailOffset = Math.Max((long)headLen, length - chunkSize);
        int tailLen = (int)(length - tailOffset);

        using var sha = SHA256.Create();
        sha.TransformBlock(metaBytes, 0, metaBytes.Length, null, 0);
        sha.TransformBlock(all, 0, headLen, null, 0);

        if (tailLen > 0)
        {
            sha.TransformFinalBlock(all, (int)tailOffset, tailLen);
        }
        else
        {
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }

        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    // --- 100MB未満: 全バイトSHA256と一致すること -------------------------------------

    [Fact]
    public void ComputeLightweightHash_小さいファイルは全バイトSHA256と一致する()
    {
        byte[] content = CreateRandomBytes(4096, seed: 1);
        string path = CreateFile("small.bin", content);

        string expected = Convert.ToHexString(SHA256.HashData(content));
        string actual = HashHelper.ComputeLightweightHash(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeLightweightHash_空ファイルも全バイトSHA256と一致する()
    {
        string path = CreateFile("empty.bin", Array.Empty<byte>());

        string expected = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        string actual = HashHelper.ComputeLightweightHash(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeLightweightHash_100MB未満数MBのファイルも全バイトSHA256と一致する()
    {
        byte[] content = CreateRandomBytes(5 * 1024 * 1024, seed: 2); // 5MB
        string path = CreateFile("few-mb.bin", content);

        string expected = Convert.ToHexString(SHA256.HashData(content));
        string actual = HashHelper.ComputeLightweightHash(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeLightweightHash_閾値1バイト未満_100MB直前も全バイトSHA256と一致する()
    {
        // 100MB(閾値)ちょうど未満はチャンク経路に入らず全バイト計算のままであることを確認する。
        // 中間はゼロ埋めで生成し、実行時間を抑える（全バイトSHA256自体は毎回100MB弱を読む）。
        long size = 100L * 1024 * 1024 - 1;
        string path = Path.Combine(_workDir, "just-under-threshold.bin");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(size);
        }

        string expected;
        using (var stream = File.OpenRead(path))
        {
            expected = Convert.ToHexString(SHA256.HashData(stream));
        }

        string actual = HashHelper.ComputeLightweightHash(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeLightweightHash_存在しないファイルは空文字列を返す()
    {
        string path = Path.Combine(_workDir, "does-not-exist.bin");
        Assert.Equal(string.Empty, HashHelper.ComputeLightweightHash(path));
    }

    // --- 100MB以上: メタデータ+先頭2MB+末尾2MBの結合ハッシュが決定的であること -----------

    [Fact]
    public void ComputeLightweightHash_100MB以上は同じファイルなら常に同じ値になる()
    {
        string hash1 = HashHelper.ComputeLightweightHash(_large.AtThresholdFilePath);
        string hash2 = HashHelper.ComputeLightweightHash(_large.AtThresholdFilePath);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA256 = 32byte = 64桁hex
    }

    [Fact]
    public void ComputeLightweightHash_100MB以上は仕様どおりのメタデータ_先頭2MB_末尾2MB結合ハッシュと一致する()
    {
        string expected = ComputeReferenceChunkedHash(_large.AtThresholdFilePath);
        string actual = HashHelper.ComputeLightweightHash(_large.AtThresholdFilePath);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeLightweightHash_100MB以上は全バイトSHA256とは異なる軽量ハッシュになる()
    {
        // 中間のゼロ埋め領域が無視されることを裏付けるため、全バイトハッシュとは一致しないことを確認する。
        string fullHash;
        using (var stream = File.OpenRead(_large.AtThresholdFilePath))
        {
            fullHash = Convert.ToHexString(SHA256.HashData(stream));
        }

        string lightweightHash = HashHelper.ComputeLightweightHash(_large.AtThresholdFilePath);

        Assert.NotEqual(fullHash, lightweightHash);
    }

    // --- 境界ケース: 先頭2MBと末尾2MBが重複しうるサイズ帯（2MB強〜4MB程度）で例外なく計算できること ---

    [Theory]
    [InlineData(2 * 1024 * 1024)]              // ちょうどChunkSize: 全体がheadとして読まれtailなし
    [InlineData(2 * 1024 * 1024 + 1)]          // headを1バイトだけ超える: tailは1バイトのみ
    [InlineData(2 * 1024 * 1024 + 512 * 1024)] // head/tailが重複しうる境界帯（約2.5MB）
    [InlineData(3 * 1024 * 1024)]              // 境界帯中央（3MB）
    [InlineData(4 * 1024 * 1024 - 1)]          // 非重複境界の直前（4MB未満）
    [InlineData(4 * 1024 * 1024)]              // ちょうど非重複境界（head/tailが接する4MB）
    public void ComputeChunkedHash_先頭末尾が重複しうる境界サイズでも例外なく計算できる(int size)
    {
        byte[] content = CreateRandomBytes(size, seed: size);
        string path = CreateFile($"boundary-{size}.bin", content);

        string actual = HashHelper.ComputeChunkedHash(path);

        Assert.Equal(64, actual.Length);
        Assert.Matches("^[0-9A-F]{64}$", actual);
    }

    [Theory]
    [InlineData(2 * 1024 * 1024 + 512 * 1024)]
    [InlineData(3 * 1024 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public void ComputeChunkedHash_境界サイズでも仕様どおりの結合ハッシュと一致し決定的である(int size)
    {
        byte[] content = CreateRandomBytes(size, seed: size + 1);
        string path = CreateFile($"boundary-ref-{size}.bin", content);

        string expected = ComputeReferenceChunkedHash(path);
        string actual1 = HashHelper.ComputeChunkedHash(path);
        string actual2 = HashHelper.ComputeChunkedHash(path);

        Assert.Equal(expected, actual1);
        Assert.Equal(actual1, actual2);
    }

    [Fact]
    public void ComputeChunkedHash_境界帯でも内容が異なれば異なるハッシュになる()
    {
        // 重複回避ロジックが先頭バッファを使い回して常に同じ値を返す退化実装になっていないことの確認。
        const int size = 2 * 1024 * 1024 + 512 * 1024; // 約2.5MB
        string pathA = CreateFile("boundary-diff-a.bin", CreateRandomBytes(size, seed: 100));
        string pathB = CreateFile("boundary-diff-b.bin", CreateRandomBytes(size, seed: 200));

        string hashA = HashHelper.ComputeChunkedHash(pathA);
        string hashB = HashHelper.ComputeChunkedHash(pathB);

        Assert.NotEqual(hashA, hashB);
    }
}
