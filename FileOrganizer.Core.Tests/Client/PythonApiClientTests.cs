using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FileOrganizer.Core.Client;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Client;

/// <summary>
/// <see cref="PythonApiClient"/> の単体テスト。
/// 実プロセス（py_service）を起動せず、<see cref="StubHttpMessageHandler"/> でHTTP層を差し替えて検証する。
/// 実プロセスと組み合わせた疎通確認は <c>FileOrganizer.Core.SmokeTest</c> を参照。
/// </summary>
public class PythonApiClientTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task AnalyzeAsync_Success_DeserializesResponse_AndSendsBearerHeaderAndBody()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();

            var responseJson = """
                {
                  "success": true,
                  "category": "請求書",
                  "metadata": { "date": "2026-08-25", "company": "合同会社テックサプライ" },
                  "confidence": 0.95
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        var request = new AnalyzeRequest
        {
            FilePath = @"C:\Users\user\Downloads\sample.pdf",
            OcrText = "請求書",
            ExtractFields = ["date", "company"],
        };

        AnalyzeResponse? result = await client.AnalyzeAsync(request);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("請求書", result.Category);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal("2026-08-25", result.Metadata?["date"]);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal(new Uri("http://127.0.0.1:54321/api/v1/analyze"), capturedRequest.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), capturedRequest.Headers.Authorization);

        Assert.NotNull(capturedBody);
        using JsonDocument sentJson = JsonDocument.Parse(capturedBody!);
        Assert.Equal(@"C:\Users\user\Downloads\sample.pdf", sentJson.RootElement.GetProperty("file_path").GetString());
    }

    [Fact]
    public async Task AnalyzeAsync_NonSuccessStatusCode_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        AnalyzeResponse? result = await client.AnalyzeAsync(new AnalyzeRequest { FilePath = "x", OcrText = "text" });

        Assert.Null(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ConnectionFailure_ReturnsNull_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("Connection refused (simulated)."));

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        AnalyzeResponse? result = await client.AnalyzeAsync(new AnalyzeRequest { FilePath = "x", OcrText = "text" });

        Assert.Null(result);
    }

    [Fact]
    public async Task AnalyzeAsync_MalformedJsonResponse_ThrowsInsteadOfSwallowing()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ this is not valid json", Encoding.UTF8, "application/json"),
            }));

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        // スキーマ不一致/不正なレスポンスは握りつぶさず例外として伝播すること。
        await Assert.ThrowsAsync<JsonException>(() => client.AnalyzeAsync(new AnalyzeRequest { FilePath = "x", OcrText = "text" }));
    }

    [Fact]
    public async Task AnalyzeAsync_CallerCancellation_PropagatesOperationCanceledException()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AnalyzeAsync(new AnalyzeRequest { FilePath = "x", OcrText = "text" }, cts.Token));
    }

    [Fact]
    public async Task AnalyzeAsync_BeforeConfigure_ThrowsInvalidOperationException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeAsync(new AnalyzeRequest { FilePath = "x", OcrText = "text" }));
    }

    [Fact]
    public async Task AnalyzeAsync_OcrTextが空ならHttp送信せずNullを返す()
    {
        bool sent = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            sent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        Assert.Null(await client.AnalyzeAsync(new AnalyzeRequest { FilePath = "x", OcrText = string.Empty }));
        Assert.False(sent);
    }

    [Fact]
    public async Task HealthCheckAsync_Success_ReturnsTrue()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/health", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        Assert.True(await client.HealthCheckAsync());
    }

    [Fact]
    public async Task HealthCheckAsync_ConnectionFailure_ReturnsFalse_DoesNotThrow()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("Connection refused (simulated)."));

        using var httpClient = new HttpClient(handler);
        using var client = new PythonApiClient(httpClient);
        client.Configure(54321, Token);

        Assert.False(await client.HealthCheckAsync());
    }

    /// <summary>テスト用の最小HttpMessageHandlerスタブ。渡されたcancellationTokenをそのままresponderへ引き渡す。</summary>
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
