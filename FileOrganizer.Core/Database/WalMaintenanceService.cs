using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Database;

/// <summary>1回のWALチェックポイント実行結果。</summary>
public sealed class WalCheckpointResult
{
    /// <summary>チェックポイントが他の接続によりブロックされ完全には完了しなかった場合true。</summary>
    public bool Busy { get; }

    /// <summary>チェックポイント開始時点のWALログのフレーム数。</summary>
    public int LogFrames { get; }

    /// <summary>実際にDB本体へ書き戻された（チェックポイントされた）フレーム数。</summary>
    public int CheckpointedFrames { get; }

    public DateTime CompletedAtUtc { get; }

    public WalCheckpointResult(bool busy, int logFrames, int checkpointedFrames, DateTime completedAtUtc)
    {
        Busy = busy;
        LogFrames = logFrames;
        CheckpointedFrames = checkpointedFrames;
        CompletedAtUtc = completedAtUtc;
    }
}

/// <summary>
/// 仕様書§7.2-4「長時間常駐安定性」（UI弱参照イベント管理と SQLite WAL チェックポイントにより、
/// アイドル時メモリが150MBを超えて肥大化しないこと）を支えるバックグラウンドメンテナンスサービス。
/// <c>AppSettings.WalCheckpointIntervalMinutes</c>（既定60分）ごとに
/// <c>PRAGMA wal_checkpoint(TRUNCATE);</c>を実行し、WALファイル（<c>*.db-wal</c>）を切り詰めて
/// DBファイル全体の肥大化を防止する。
/// </summary>
/// <remarks>
/// <c>TRUNCATE</c>モードは、可能な限りチェックポイント後にWALファイルを0バイトへ切り詰める
/// （<c>FULL</c>と異なりファイルサイズ自体を縮小する）ため、長時間常駐でWALファイルが際限なく
/// 育ち続ける事態を防ぐ。他接続がWALスナップショットを保持中等でチェックポイントが完了しきらない
/// 場合は<see cref="WalCheckpointResult.Busy"/>がtrueになるが、これは異常ではなく次回実行時に
/// 再試行される。フォルダ数・ファイル数に関わらずバックグラウンドスレッドは1本のみ生成する
/// （集約ワーカー方式、ファイル単位のTimerは使わない）。
/// </remarks>
public sealed class WalMaintenanceService : IDisposable
{
    /// <summary>AppSettings.WalCheckpointIntervalMinutesの既定値（60分）と同値。</summary>
    public const int DefaultCheckpointIntervalMinutes = 60;

    private readonly string _connectionString;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private bool _disposed;

    /// <summary>チェックポイント実行（定期・手動いずれも）が完了するたびに発火する（観測・ログ用、任意購読）。</summary>
    public event EventHandler<WalCheckpointResult>? CheckpointCompleted;

    /// <summary>チェックポイント実行中に例外が発生した場合に発火する（ワーカー自体は継続する）。</summary>
    public event EventHandler<Exception>? CheckpointFailed;

    /// <param name="connectionString">
    /// 対象DBの接続文字列（<see cref="DatabaseInitializer.BuildConnectionString"/>で生成したもの）。
    /// 事前にWALモードが有効化されている（<see cref="DatabaseInitializer.InitializeAsync"/>済み）ことを前提とする。
    /// </param>
    /// <param name="checkpointIntervalMinutes">チェックポイント間隔（分）。既定は60分。</param>
    public WalMaintenanceService(string connectionString, int checkpointIntervalMinutes = DefaultCheckpointIntervalMinutes)
        : this(connectionString, ValidateMinutes(checkpointIntervalMinutes))
    {
    }

    /// <summary>
    /// 分未満の間隔を指定できる内部向けコンストラクタ。単体テストでバックグラウンドループの
    /// 定期実行そのものを短時間で検証できるようにするためのもの（本番では分単位のみを公開する）。
    /// </summary>
    internal WalMaintenanceService(string connectionString, TimeSpan checkpointInterval)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("接続文字列を空にすることはできません。", nameof(connectionString));
        if (checkpointInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(checkpointInterval), checkpointInterval, "間隔は正の値である必要があります。");

        _connectionString = connectionString;
        _interval = checkpointInterval;

        // 長時間常駐時もバックグラウンドスレッドを1本のみ生成する集約ワーカー方式。
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    private static TimeSpan ValidateMinutes(int checkpointIntervalMinutes)
    {
        if (checkpointIntervalMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkpointIntervalMinutes), checkpointIntervalMinutes, "間隔は正の値である必要があります。");
        return TimeSpan.FromMinutes(checkpointIntervalMinutes);
    }

    /// <summary>
    /// 次回の定期実行を待たず、即座に<c>PRAGMA wal_checkpoint(TRUNCATE);</c>を実行する。
    /// 定期実行・外部からの明示的な要求（アプリ終了直前のクリーンアップ等）の両方から呼ばれる。
    /// </summary>
    public async Task<WalCheckpointResult> RunCheckpointNowAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        bool busy = false;
        int logFrames = 0;
        int checkpointedFrames = 0;

        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            // PRAGMA wal_checkpoint は (busy, log, checkpointed) の1行を返す。
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                busy = reader.GetInt32(0) != 0;
                logFrames = reader.GetInt32(1);
                checkpointedFrames = reader.GetInt32(2);
            }
        }

        var result = new WalCheckpointResult(busy, logFrames, checkpointedFrames, DateTime.UtcNow);
        CheckpointCompleted?.Invoke(this, result);
        return result;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await RunCheckpointNowAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // チェックポイント失敗（一時的なDBロック等）でワーカー自体は止めず、次回間隔で再試行する。
                    CheckpointFailed?.Invoke(this, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose時の正常終了。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Dispose中の例外は無視（キャンセルによる正常終了を含む）。
        }
        _cts.Dispose();
    }
}
