namespace FileOrganizer.Core.Client;

/// <summary>
/// <see cref="PythonProcessManager.ProcessCrashed"/>で通知される異常終了情報。
/// 仕様書§7.2-3「推論中のOOM等でPythonプロセスが異常終了した場合」の検知結果。
/// ハンドシェイク完了後（＝正常稼働に入った後）に、C#側から意図せず（<c>Dispose</c>等を経由せず）
/// プロセスが終了したことを表す。ハンドシェイク完了前の終了は<see cref="PythonProcessManager.StartAsync(System.Threading.CancellationToken)"/>
/// 自体の<see cref="System.InvalidOperationException"/>として表現されるため、本イベントの対象外。
/// </summary>
public sealed class PythonProcessCrashedEventArgs : EventArgs
{
    /// <summary>異常終了したプロセスのPID。</summary>
    public required int ProcessId { get; init; }

    /// <summary>プロセスの終了コード（取得できなかった場合は<c>-1</c>）。</summary>
    public required int ExitCode { get; init; }

    /// <summary>終了までに出力されたstderrの末尾（取得できなかった・空だった場合は<c>null</c>）。</summary>
    public string? StderrTail { get; init; }
}
