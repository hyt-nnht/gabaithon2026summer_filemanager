// 仕様書§7.2-1「負荷耐性」（1,000件のファイルを一括投入しても、単一集約ポーリングワーカーにより
// スレッド枯渇を起こさず、2重静止待機キューと再走査により欠落・多重移動なく正常終了すること）の
// 実測ツール。FileOrganizer.Core.Tests（xUnit、テストランナー自身が多数のワーカースレッドを持つ）
// ではプロセス全体のスレッド数/CPU使用率を汚染してしまうため、専用の単独プロセスとして計測する。
//
// 使い方:
//   dotnet run --project FileOrganizer.Core.SmokeTest -- --load-test [--count <N=1000>]
//     [--stability-interval-ms <ms=750>] [--timeout-seconds <s=60>]

using System.Diagnostics;
using FileOrganizer.Core.Watcher;

internal static class LoadTestSmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        int fileCount = 1000;
        int stabilityIntervalMs = FileStabilityDetector.DefaultStabilityCheckIntervalMs; // 本番既定値(750ms)と同一条件で計測する
        int timeoutSeconds = 60;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count" when i + 1 < args.Length:
                    fileCount = int.Parse(args[++i]);
                    break;
                case "--stability-interval-ms" when i + 1 < args.Length:
                    stabilityIntervalMs = int.Parse(args[++i]);
                    break;
                case "--timeout-seconds" when i + 1 < args.Length:
                    timeoutSeconds = int.Parse(args[++i]);
                    break;
            }
        }

        Console.WriteLine("=== 負荷耐性 計測ツール（仕様書§7.2-1） ===");
        Console.WriteLine($"[config] count={fileCount}, stabilityIntervalMs={stabilityIntervalMs}, timeoutSeconds={timeoutSeconds}");

        string workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerLoadTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            Console.WriteLine($"[setup] {fileCount}件のダミーファイルを作成中... ({workDir})");
            var paths = new string[fileCount];
            for (int i = 0; i < fileCount; i++)
            {
                string path = Path.Combine(workDir, $"loadtest-{i:D5}.txt");
                File.WriteAllText(path, $"load-test-file-{i}");
                paths[i] = path;
            }

            using var detector = new FileStabilityDetector(stabilityIntervalMs);

            var stabilizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateCount = 0;
            var stabilizedLock = new object();
            var allStabilizedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            detector.FileStabilized += (_, e) =>
            {
                lock (stabilizedLock)
                {
                    if (!stabilizedPaths.Add(e.Metadata.FullPath))
                    {
                        duplicateCount++; // 同一パスが2回安定通知された = 多重処理の疑い
                    }

                    if (stabilizedPaths.Count >= fileCount)
                    {
                        allStabilizedTcs.TrySetResult();
                    }
                }
            };

            var process = Process.GetCurrentProcess();
            int peakThreadCount = process.Threads.Count;
            TimeSpan cpuStart = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();

            using var samplerCts = new CancellationTokenSource();
            Task samplerTask = Task.Run(async () =>
            {
                while (!samplerCts.IsCancellationRequested)
                {
                    process.Refresh();
                    int current = process.Threads.Count;
                    if (current > peakThreadCount)
                    {
                        peakThreadCount = current;
                    }

                    try
                    {
                        await Task.Delay(100, samplerCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            Console.WriteLine("[run] 1,000件を一括でEnqueue（単一集約ワーカーへ投入）...");
            foreach (string path in paths)
            {
                detector.Enqueue(path); // FileSystemWatcherの一括イベント相当。ファイルごとのTimer/Taskは生成しない。
            }

            Task completed = await Task.WhenAny(allStabilizedTcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            stopwatch.Stop();

            samplerCts.Cancel();
            try { await samplerTask.ConfigureAwait(false); } catch { /* ignore */ }

            process.Refresh();
            TimeSpan cpuDelta = process.TotalProcessorTime - cpuStart;
            double cpuPercent = stopwatch.Elapsed.TotalSeconds > 0
                ? cpuDelta.TotalSeconds / stopwatch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100.0
                : 0.0;

            bool allStabilizedInTime = completed == allStabilizedTcs.Task;
            int stabilizedCount;
            lock (stabilizedLock)
            {
                stabilizedCount = stabilizedPaths.Count;
            }

            Console.WriteLine();
            Console.WriteLine("--- 結果 ---");
            Console.WriteLine($"[result] 投入件数            = {fileCount}");
            Console.WriteLine($"[result] 安定検知件数          = {stabilizedCount} (欠落 = {fileCount - stabilizedCount})");
            Console.WriteLine($"[result] 多重検知件数          = {duplicateCount}");
            Console.WriteLine($"[result] 全件安定検知まで      = {(allStabilizedInTime ? $"{stopwatch.ElapsedMilliseconds}ms" : $"タイムアウト({timeoutSeconds}秒)")}");
            Console.WriteLine($"[result] ピークスレッド数       = {peakThreadCount}");
            Console.WriteLine($"[result] CPU使用率(平均, 全コア対比) = {cpuPercent:F1}%");
            Console.WriteLine($"[result] 残留Pending件数(0であるべき) = {detector.PendingCount}");

            bool pass = allStabilizedInTime && stabilizedCount == fileCount && duplicateCount == 0 && detector.PendingCount == 0;
            Console.WriteLine();
            Console.WriteLine(pass ? "[summary] PASS" : "[summary] FAIL");
            return pass ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* 後始末失敗は無視 */ }
        }
    }
}
