using System.Net;
using FileOrganizer.Core.Client;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Client;

/// <summary>
/// <see cref="ModelDownloadManager"/> の単体テスト。
/// 仕様書§3.1「デモ用事前配置モデルフラグ」の判定ロジック（<see cref="ModelDownloadManager.CheckPreloadedModelAsync"/>）
/// を実ファイルI/O（一時フォルダ）で検証し、オンデマンドダウンロード（<see cref="ModelDownloadManager.DownloadModelAsync"/>）を
/// <see cref="StubHttpMessageHandler"/>でHTTP通信をスタブ化して検証する。
/// </summary>
public class ModelDownloadManagerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "ModelDownloadManager", Guid.NewGuid().ToString("N"));
    private readonly ModelDownloadManager _manager = new();

    public ModelDownloadManagerTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        _manager.Dispose();
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

    private string CreateModelFile(long sizeBytes)
    {
        string path = Path.Combine(_workDir, "model.gguf");
        using (var fs = new FileStream(path, FileMode.Create))
        {
            fs.SetLength(sizeBytes);
        }
        return path;
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_UsePreloadedSlmModelがfalseならDisabledを返す()
    {
        var settings = new AppSettings { UsePreloadedSlmModel = false, SlmModelPath = CreateModelFile(ModelDownloadManager.ExpectedModelSizeBytes) };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.Disabled, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_SlmModelPathが未設定ならPathNotConfiguredを返す()
    {
        var settings = new AppSettings { UsePreloadedSlmModel = true, SlmModelPath = "" };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.PathNotConfigured, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_指定パスにファイルが無ければFileNotFoundを返す()
    {
        var settings = new AppSettings
        {
            UsePreloadedSlmModel = true,
            SlmModelPath = Path.Combine(_workDir, "missing-model.gguf"),
        };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.FileNotFound, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_期待サイズの半分未満ならSizeTooSmallを返す()
    {
        long tooSmallSize = (long)(ModelDownloadManager.ExpectedModelSizeBytes * 0.1);
        var settings = new AppSettings
        {
            UsePreloadedSlmModel = true,
            SlmModelPath = CreateModelFile(tooSmallSize),
        };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.SizeTooSmall, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(tooSmallSize, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_期待サイズ相当のファイルが存在すればReadyを返す()
    {
        var settings = new AppSettings
        {
            UsePreloadedSlmModel = true,
            SlmModelPath = CreateModelFile(ModelDownloadManager.ExpectedModelSizeBytes),
        };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.Ready, result.Status);
        Assert.True(result.IsReady);
        Assert.Equal(settings.SlmModelPath, result.ModelPath);
        Assert.Equal(ModelDownloadManager.ExpectedModelSizeBytes, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_settingsがnullの場合は例外を投げる()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _manager.CheckPreloadedModelAsync(null!));
    }

    [Fact]
    public async Task DownloadModelAsync_正常応答なら一時ファイル経由でdestinationPathへ保存し進捗を100まで報告する()
    {
        long size = (long)(ModelDownloadManager.ExpectedModelSizeBytes * 0.6); // MinimumSizeRatio(0.5)以上
        byte[] content = CreateDummyContent(size);
        using var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.OkResponse(content));
        using var httpClient = new HttpClient(handler);
        using var manager = new ModelDownloadManager(httpClient);

        string destinationPath = Path.Combine(_workDir, "nested", "downloaded-model.gguf");
        var progressValues = new List<double>();
        var progress = new SyncProgress<double>(v => progressValues.Add(v));

        ModelDownloadResult result = await manager.DownloadModelAsync(destinationPath, progress);

        Assert.True(result.Success);
        Assert.Equal(destinationPath, result.ModelPath);
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(size, new FileInfo(destinationPath).Length);
        Assert.False(File.Exists(destinationPath + ".download"), "完了後は一時ファイルが残っていないこと。");
        Assert.Contains(1.0, progressValues);
        Assert.All(progressValues, v => Assert.InRange(v, 0.0, 1.0));
    }

    [Fact]
    public async Task DownloadModelAsync_サイズ不足の応答ならSuccessFalseで一時ファイルを削除する()
    {
        long tooSmallSize = (long)(ModelDownloadManager.ExpectedModelSizeBytes * 0.1); // MinimumSizeRatio未満
        byte[] content = CreateDummyContent(tooSmallSize);
        using var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.OkResponse(content));
        using var httpClient = new HttpClient(handler);
        using var manager = new ModelDownloadManager(httpClient);

        string destinationPath = Path.Combine(_workDir, "too-small-model.gguf");

        ModelDownloadResult result = await manager.DownloadModelAsync(destinationPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(destinationPath));
        Assert.False(File.Exists(destinationPath + ".download"));
    }

    [Fact]
    public async Task DownloadModelAsync_非2xx応答ならSuccessFalseでエラーメッセージを返す()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        using var manager = new ModelDownloadManager(httpClient);

        string destinationPath = Path.Combine(_workDir, "not-found-model.gguf");

        ModelDownloadResult result = await manager.DownloadModelAsync(destinationPath);

        Assert.False(result.Success);
        Assert.Contains("404", result.ErrorMessage);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadModelAsync_通信エラーならSuccessFalseでエラーメッセージを返す()
    {
        using var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("接続失敗（テスト）"));
        using var httpClient = new HttpClient(handler);
        using var manager = new ModelDownloadManager(httpClient);

        string destinationPath = Path.Combine(_workDir, "network-error-model.gguf");

        ModelDownloadResult result = await manager.DownloadModelAsync(destinationPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadModelAsync_キャンセルされた場合はOperationCanceledExceptionを投げ一時ファイルを削除する()
    {
        long size = (long)(ModelDownloadManager.ExpectedModelSizeBytes * 0.6);
        byte[] content = CreateDummyContent(size);
        using var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.OkResponse(content));
        using var httpClient = new HttpClient(handler);
        using var manager = new ModelDownloadManager(httpClient);

        string destinationPath = Path.Combine(_workDir, "cancelled-model.gguf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.DownloadModelAsync(destinationPath, ct: cts.Token));

        Assert.False(File.Exists(destinationPath + ".download"));
    }

    [Fact]
    public async Task DownloadModelAsync_destinationPathが空の場合は例外を投げる()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.DownloadModelAsync(""));
    }

    private static byte[] CreateDummyContent(long size)
    {
        // テスト用の内容。実サイズ検証（MinimumSizeRatio判定）のみを目的とし、内容自体に意味は無い。
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes.AsSpan(0, (int)Math.Min(size, 4096)));
        return bytes;
    }

}

/// <summary>
/// テスト用の同期<see cref="IProgress{T}"/>実装。<see cref="Progress{T}"/>は
/// <see cref="System.Threading.SynchronizationContext"/>経由で非同期にコールバックするため、
/// テストで確定的に値を検証できるよう<see cref="Report"/>を呼び出しスレッドで同期実行する。
/// </summary>
internal sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

/// <summary>
/// テスト用の最小HttpMessageHandler。リクエストごとに<paramref name="respond"/>を呼び出し、
/// 応答（または例外）を差し替える。<see cref="ModelDownloadManager"/>の実HTTP通信を発生させずに検証するために使う。
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage OkResponse(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentLength = content.Length;
        return response;
    }
}
