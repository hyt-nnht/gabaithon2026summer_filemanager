using System.Net;
using System.Net.Sockets;
using System.Text;
using FileOrganizer.Core.Client;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Client;

/// <summary>
/// 仕様書§7.2-5「IPCセキュリティ: ローカルAPIは Port 0 動的割当および Bearer Token 認証により、
/// 他ローカルプロセスからの不正アクセスを遮断すること」のうち、Bearer Token不一致リクエストの
/// 拒否を検証する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PythonProcessManager"/>（32文字ランダムHexトークン生成、<see cref="PythonProcessManagerTests"/>で検証済み）と
/// <see cref="PythonApiClient"/>（Bearerヘッダー送信、<c>PythonApiClientTests.AnalyzeAsync_Success_...</c>で検証済み）が
/// C#側の役割のすべてであり、実際に「不一致トークンを拒否する」判定自体はサーバー側（py_service）の責務
/// （<c>py_service/src/file_analyzer/api/app.py</c>の<c>authorize()</c>、401応答）である。
/// </para>
/// <para>
/// 本クラスは、py_serviceの実行に必要な依存関係（fastapi/uvicorn/onnxruntime/llama-cpp-python等、
/// <c>py_service/requirements.txt</c>）がこのリポジトリのC#テスト環境には無い前提で、
/// py_serviceの認証コントラクト（<c>Authorization: Bearer &lt;token&gt;</c>、不一致/欠落時401、
/// 対象は health/analyze 双方 -- app.py 82-93行目・91-130行目を参照）を模した最小限の
/// <see cref="HttpListener"/>スタンドインサーバーに対して<see cref="PythonApiClient"/>を実際に通信させ、
/// 「不一致トークンのリクエストは拒否（失敗）として扱われ、例外で落ちたりトークンが漏洩したりしない」
/// というC#側トランスポート層の契約を検証する。実py_serviceを用いたフルスタックE2E確認の手順は
/// 検証手順書（<c>docs/</c>配下の統合テスト手順書）に別途記載する。
/// </para>
/// </remarks>
public class PythonApiClientSecurityTests
{
    private const string CorrectToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WrongToken = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task HealthCheckAsync_正しいTokenなら成功する()
    {
        using var server = new StandInAuthServer(CorrectToken);
        using var client = new PythonApiClient();
        client.Configure(server.Port, CorrectToken);

        Assert.True(await client.HealthCheckAsync());
        Assert.Equal(1, server.AuthorizedRequestCount);
        Assert.Equal(0, server.RejectedRequestCount);
    }

    [Fact]
    public async Task HealthCheckAsync_Tokenが不一致なら拒否され例外を投げずfalseを返す()
    {
        using var server = new StandInAuthServer(CorrectToken);
        using var client = new PythonApiClient();
        client.Configure(server.Port, WrongToken); // わざと不一致のTokenを設定

        bool result = await client.HealthCheckAsync();

        Assert.False(result); // 401を例外にせず、失敗として表現する（PythonApiClientの既存契約どおり）。
        Assert.Equal(0, server.AuthorizedRequestCount);
        Assert.Equal(1, server.RejectedRequestCount);
    }

    [Fact]
    public async Task AnalyzeAsync_正しいTokenなら成功しレスポンスを取得できる()
    {
        using var server = new StandInAuthServer(CorrectToken);
        using var client = new PythonApiClient();
        client.Configure(server.Port, CorrectToken);

        AnalyzeResponse? result = await client.AnalyzeAsync(new AnalyzeRequest
        {
            FilePath = @"C:\Demo\sample.pdf",
            OcrText = "請求書",
        });

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(1, server.AuthorizedRequestCount);
    }

    [Fact]
    public async Task AnalyzeAsync_Tokenが不一致なら拒否され例外を投げずnullを返す()
    {
        using var server = new StandInAuthServer(CorrectToken);
        using var client = new PythonApiClient();
        client.Configure(server.Port, WrongToken);

        AnalyzeResponse? result = await client.AnalyzeAsync(new AnalyzeRequest
        {
            FilePath = @"C:\Demo\sample.pdf",
            OcrText = "請求書",
        });

        Assert.Null(result);
        Assert.Equal(1, server.RejectedRequestCount);
    }

    [Fact]
    public async Task AnalyzeAsync_Authorizationヘッダーが無い場合も拒否される()
    {
        using var server = new StandInAuthServer(CorrectToken);
        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        // PythonApiClient.Configure()を経由しない = Authorizationヘッダー無し、を直接再現する。

        HttpResponseMessage response = await httpClient.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, server.RejectedRequestCount);
    }

    /// <summary>
    /// py_serviceの<c>authorize()</c>依存関係（<c>Authorization: Bearer &lt;token&gt;</c>不一致/欠落時401）を
    /// 模した最小限のスタンドインHTTPサーバー。<see cref="HttpListener"/>のみを使い、実py_service（FastAPI/
    /// uvicorn等の重量級依存）を必要としない。
    /// </summary>
    private sealed class StandInAuthServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _expectedAuthorizationHeader;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoopTask;
        private int _authorizedCount;
        private int _rejectedCount;

        public int Port { get; }

        public int AuthorizedRequestCount => Volatile.Read(ref _authorizedCount);
        public int RejectedRequestCount => Volatile.Read(ref _rejectedCount);

        public StandInAuthServer(string expectedToken)
        {
            Port = GetFreeTcpPort();
            _expectedAuthorizationHeader = $"Bearer {expectedToken}";

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening)
                {
                    return; // Stop()によるシャットダウン。
                }

                _ = HandleRequestAsync(context); // 複数同時リクエストに対応するためawaitしない。
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                // py_service app.py の authorize(): Authorization ヘッダーが無い、または
                // "Bearer <正しいtoken>" と完全一致しない場合は401（実装はhmac.compare_digestだが、
                // ここでは通常の文字列比較で契約の再現のみ行う）。
                string? authorizationHeader = context.Request.Headers["Authorization"];
                bool authorized = authorizationHeader == _expectedAuthorizationHeader;

                if (!authorized)
                {
                    Interlocked.Increment(ref _rejectedCount);
                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    byte[] body = Encoding.UTF8.GetBytes("""{"detail":"Bearer token is invalid"}""");
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    context.Response.Close();
                    return;
                }

                Interlocked.Increment(ref _authorizedCount);

                string path = context.Request.Url!.AbsolutePath;
                if (path == PythonApiClient.HealthEndpointPath)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.Close();
                }
                else if (path == PythonApiClient.AnalyzeEndpointPath)
                {
                    byte[] body = Encoding.UTF8.GetBytes("""{"success":true,"category":"請求書","metadata":{},"confidence":0.9}""");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    context.Response.Close();
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                }
            }
            catch (ObjectDisposedException)
            {
                // Dispose()中の競合は無視。
            }
            catch (HttpListenerException)
            {
                // Dispose()中の競合は無視。
            }
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try { _acceptLoopTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
