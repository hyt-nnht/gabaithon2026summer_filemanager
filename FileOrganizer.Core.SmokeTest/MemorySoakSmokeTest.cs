// 仕様書§7.2-4「長時間常駐安定性」（UI弱参照イベント管理と SQLite WAL チェックポイントにより、
// アイドル時メモリが150MBを超えて肥大化しないこと）のうち、FileOrganizer.Core側で制御可能な部分
// （SQLite WALチェックポイント・単一集約ワーカーの継続稼働）を短時間の加速実行で検証するツール。
//
// 【重要な限界】本ツールは分オーダーの短時間実行であり、仕様が想定する「長時間（数時間〜終日）常駐」の
// リーク検出そのものを代替するものではない。150MB絶対値のスナップショット判定（このツールで直接検証可能）と、
// 長時間トレンド（リークが無いこと）の判定は別物であり、後者は本ドキュメントの手順に従って別途、
// 実機で数時間オーダーの手動計測を行うこと。また「UI弱参照イベント管理」はFileOrganizer.UI未実装のため
// 本ツールの対象外（UI実装後に別途検証要）。
//
// 使い方:
//   dotnet run --project FileOrganizer.Core.SmokeTest -- --memory-soak [--duration-seconds <s=120>]
//     [--batch-size <n=200>] [--batch-interval-seconds <s=5>]

using System.Diagnostics;
using FileOrganizer.Core.Database;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

internal static class MemorySoakSmokeTest
{
    private const long ThresholdBytes = 150L * 1024 * 1024; // 仕様書§7.2-4「150MBを超えない」

    public static async Task<int> RunAsync(string[] args)
    {
        int durationSeconds = 120;
        int batchSize = 200;
        int batchIntervalSeconds = 5;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--duration-seconds" when i + 1 < args.Length:
                    durationSeconds = int.Parse(args[++i]);
                    break;
                case "--batch-size" when i + 1 < args.Length:
                    batchSize = int.Parse(args[++i]);
                    break;
                case "--batch-interval-seconds" when i + 1 < args.Length:
                    batchIntervalSeconds = int.Parse(args[++i]);
                    break;
            }
        }

        Console.WriteLine("=== 長時間常駐安定性 短時間加速プロキシ計測（仕様書§7.2-4） ===");
        Console.WriteLine($"[config] durationSeconds={durationSeconds}, batchSize={batchSize}, batchIntervalSeconds={batchIntervalSeconds}");
        Console.WriteLine("[caveat] 本計測は分オーダーの加速実行であり、長時間トレンド(リーク有無)の代替にはならない。150MB絶対値の直接確認のみを行う。");

        string workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerMemorySoak", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string dbPath = Path.Combine(workDir, "history.db");
        string connectionString = DatabaseInitializer.BuildConnectionString(dbPath);

        try
        {
            await new DatabaseInitializer(connectionString).InitializeAsync();
            IHistoryRepository repository = new SqliteHistoryRepository(connectionString);

            int checkpointCount = 0;
            // 公開APIは分単位のみ（最短1分）。本計測の実行時間内に最低1回は定期チェックポイントが走る設定にする。
            using var walMaintenanceService = new WalMaintenanceService(connectionString, checkpointIntervalMinutes: 1);
            walMaintenanceService.CheckpointCompleted += (_, _) => Interlocked.Increment(ref checkpointCount);

            using var detector = new FileStabilityDetector();
            int stabilizedTotal = 0;
            detector.FileStabilized += (_, _) => Interlocked.Increment(ref stabilizedTotal);

            var samples = new List<(TimeSpan Elapsed, long WorkingSetBytes)>();
            var stopwatch = Stopwatch.StartNew();

            using var samplerCts = new CancellationTokenSource();
            Task samplerTask = Task.Run(async () =>
            {
                while (!samplerCts.IsCancellationRequested)
                {
                    samples.Add((stopwatch.Elapsed, Environment.WorkingSet));
                    try { await Task.Delay(1000, samplerCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });

            Task producerTask = Task.Run(async () =>
            {
                int fileSeq = 0;
                long historySeq = 0;
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
                {
                    // 背景の監視アクティビティ相当: 一定間隔でファイル安定検知パイプラインへ投入する。
                    for (int i = 0; i < batchSize; i++)
                    {
                        string path = Path.Combine(workDir, $"soak-{fileSeq:D6}.txt");
                        File.WriteAllText(path, "x");
                        detector.Enqueue(path);
                        fileSeq++;
                    }

                    // 背景のDB書き込みアクティビティ相当: WALにフレームを積み増してチェックポイント対象を作る。
                    for (int i = 0; i < 20; i++)
                    {
                        long id = await repository.InsertAsync(new HistoryRecord
                        {
                            OperationId = Guid.NewGuid().ToString("N"),
                            OpType = OperationType.Move,
                            SourcePath = $@"C:\watch\soak-{historySeq}.txt",
                            DestinationPath = $@"D:\organized\soak-{historySeq}.txt",
                            FileSizeBytes = 1,
                            FileLastModifiedUtc = DateTime.UtcNow,
                            LightweightHash = "HASH",
                            State = OperationState.Planned,
                        });
                        await repository.UpdateStateAsync(id, OperationState.Completed);
                        historySeq++;
                    }

                    try { await Task.Delay(TimeSpan.FromSeconds(batchIntervalSeconds)).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });

            await producerTask;
            samplerCts.Cancel();
            try { await samplerTask.ConfigureAwait(false); } catch { /* ignore */ }

            // 「アイドル時メモリ」判定に合わせ、最終チェックポイント+GCを行った上での“落ち着いた”値も別途記録する。
            await walMaintenanceService.RunCheckpointNowAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long settledWorkingSet = Environment.WorkingSet;

            long peak = samples.Count > 0 ? samples.Max(s => s.WorkingSetBytes) : 0;
            long first = samples.Count > 0 ? samples[0].WorkingSetBytes : 0;
            long last = samples.Count > 0 ? samples[^1].WorkingSetBytes : 0;

            Console.WriteLine();
            Console.WriteLine("--- 結果 ---");
            Console.WriteLine($"[result] サンプル数              = {samples.Count}");
            Console.WriteLine($"[result] 開始時WorkingSet         = {first / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine($"[result] 終了時WorkingSet         = {last / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine($"[result] ピークWorkingSet         = {peak / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine($"[result] チェックポイント+GC後の値  = {settledWorkingSet / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine($"[result] WALチェックポイント実行回数 = {checkpointCount}");
            Console.WriteLine($"[result] 安定検知累計件数           = {stabilizedTotal}");
            Console.WriteLine($"[result] 残留Pending件数            = {detector.PendingCount}");
            Console.WriteLine($"[result] しきい値                  = {ThresholdBytes / 1024.0 / 1024.0:F0} MB");

            bool pass = peak < ThresholdBytes && settledWorkingSet < ThresholdBytes;
            Console.WriteLine();
            Console.WriteLine(pass ? "[summary] PASS（短時間加速プロキシとしての絶対値判定のみ。長時間トレンドは別途手動実施要）" : "[summary] FAIL");
            return pass ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* 後始末失敗は無視 */ }
        }
    }
}
