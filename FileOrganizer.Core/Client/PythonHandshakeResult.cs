namespace FileOrganizer.Core.Client;

/// <summary>
/// 起動ハンドシェイク（AI_IMPLEMENTATION_GUIDE.md §3.1）完了時に確定する接続情報。
/// <see cref="FileOrganizer.Shared.Contracts.IPythonApiClient.Configure"/> にそのまま渡せる。
/// </summary>
/// <param name="Port">Pythonプロセスが標準出力へ報告した実バインドポート（"PORT: {number}"行由来）。</param>
/// <param name="Token">起動時にC#側が生成し、環境変数 ORGANIZER_IPC_TOKEN で渡したBearerトークン。</param>
public sealed record PythonHandshakeResult(int Port, string Token)
{
    /// <summary>解析済みAPIベースURI（例: http://127.0.0.1:54321）。</summary>
    public Uri BaseUri => new($"http://127.0.0.1:{Port}");
}
