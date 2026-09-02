using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Client;

/// <summary>
/// py_service（Embedded Python FastAPIサービス）呼び出し用HTTPクライアント。
/// AI_IMPLEMENTATION_GUIDE.md §3.2 / CONTRACTS.md §4 準拠。
/// 仕様書§7.2-5「IPCセキュリティ: Port 0動的割当およびBearer Token認証」の呼び出し側実装。
/// </summary>
/// <remarks>
/// 起動ハンドシェイク（<see cref="PythonProcessManager"/>）で確定した
/// <c>Port</c>/<c>Token</c>を<see cref="Configure"/>に渡してから使用する。
/// Pythonプロセスが異常終了しリスポーンした場合も、新しいPort/Tokenで再度
/// <see cref="Configure"/>を呼び直せば同一インスタンスを継続利用できる。
///
/// タイムアウト・接続失敗（サーバー未起動、ポート不一致等）はメソッドの戻り値
/// （<c>false</c> / <c>null</c>）として表現し、例外は投げない。
/// 一方、呼び出し元が渡した<see cref="CancellationToken"/>によるキャンセル、
/// レスポンスJSONのスキーマ不一致（<see cref="JsonException"/>）、
/// <see cref="Configure"/>未呼び出しなどの呼び出し側の誤りは例外として伝播させる
/// （「タイムアウト・接続失敗時はfalse/nullを返し例外を握りつぶさず呼び出し元に伝播できる設計にする」）。
///
/// 【要合意】ヘルスチェック用エンドポイントはAI_IMPLEMENTATION_GUIDE.md §4.3で
/// 「別途仕様を確定すること」とされており未確定。本実装では暫定的に
/// <c>GET /api/v1/health</c>（analyzeと同じ /api/v1 プレフィックス）を仮定している。
/// Python担当者との合意が取れ次第、<see cref="HealthEndpointPath"/>を更新すること。
/// </remarks>
public sealed class PythonApiClient : IPythonApiClient, IDisposable
{
    /// <summary>AI_IMPLEMENTATION_GUIDE.md §3.2で確定済みのエンドポイント。</summary>
    public const string AnalyzeEndpointPath = "/api/v1/analyze";

    /// <summary>暫定パス。Python担当者と要合意（remarks参照）。</summary>
    public const string HealthEndpointPath = "/api/v1/health";

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _configured;
    private bool _disposed;

    /// <summary>
    /// 新規に内部管理のHttpClientを生成するコンストラクタ。
    /// </summary>
    /// <param name="requestTimeout">
    /// 1リクエストあたりのタイムアウト。既定30秒（SLM推論を伴う/api/v1/analyzeは
    /// ルールベースのみのフォールバックより時間がかかりうるため余裕を持たせている）。
    /// </param>
    public PythonApiClient(TimeSpan? requestTimeout = null)
        : this(new HttpClient(), ownsHttpClient: true, requestTimeout)
    {
    }

    /// <summary>
    /// テスト等でHttpClient（差し替え可能なHttpMessageHandlerを持つもの）を注入するコンストラクタ。
    /// 渡した<paramref name="httpClient"/>の所有権は呼び出し元に残り、Disposeでは破棄しない。
    /// </summary>
    public PythonApiClient(HttpClient httpClient, TimeSpan? requestTimeout = null)
        : this(httpClient, ownsHttpClient: false, requestTimeout)
    {
    }

    private PythonApiClient(HttpClient httpClient, bool ownsHttpClient, TimeSpan? requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _httpClient.Timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public void Configure(int port, string bearerToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "portは0〜65535の範囲で指定してください。");
        }

        _httpClient.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        _configured = true;
    }

    /// <inheritdoc />
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        EnsureConfigured();

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(HealthEndpointPath, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // 接続失敗（プロセス未起動・ポート不一致・DNS/TCPエラー等）。
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 呼び出し元のctではなく、HttpClient.Timeout側の内部タイムアウト。
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<AnalyzeResponse?> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .PostAsJsonAsync(AnalyzeEndpointPath, request, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            // JSONスキーマ不一致（JsonException）はここで握りつぶさず、呼び出し元へ伝播させる。
            return await response.Content
                .ReadFromJsonAsync<AnalyzeResponse>(ResponseJsonOptions, ct)
                .ConfigureAwait(false);
        }
    }

    private void EnsureConfigured()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            throw new InvalidOperationException(
                $"{nameof(PythonApiClient)}.{nameof(Configure)}(port, bearerToken) が呼ばれる前にAPIを呼び出しました。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
