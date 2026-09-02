// PythonProcessManager + PythonApiClient の疎通確認用コンソールツール。
// 仕様書§7.2-5「Port 0動的割当およびBearer Token認証」の起動〜API呼び出しまでを
// 実プロセス相手に一通り確認するための手動実行ツール（自動テストではない）。
//
// 使い方:
//   1) py_serviceを自分でJobObject経由で起動して確認する場合（既定）:
//        dotnet run --project FileOrganizer.Core.SmokeTest -- [--repo-root <path>] [--python <exe>] [--file <path>]
//      py_service/main.py が "PORT: {number}" をstdoutへ出力し、
//      ORGANIZER_IPC_TOKEN を読む構成になっている前提（未対応の場合は下記ATTACHモードを使う）。
//
//   2) 既に起動済みのサーバー（Phase 0のPythonチーム提供モック等）へ接続して確認する場合:
//        dotnet run --project FileOrganizer.Core.SmokeTest -- --attach --port <port> --token <token> [--file <path>]
//
//   3) SafeFileOperations（ごみ箱退避・Cross-Volume移動・キャンセル）の疎通確認:
//        dotnet run --project FileOrganizer.Core.SmokeTest -- --shell-ops [--drive-a <path>] [--drive-b <path>]
//      --drive-a/--drive-b省略時は起動しているドライブから自動選択する（2台ないとCross-Volumeは検証できない）。

using System.Diagnostics;
using FileOrganizer.Core.Client;
using FileOrganizer.Core.Win32;
using FileOrganizer.Shared.Models;

if (args.Length > 0 && args[0] == "--shell-ops")
{
    return await ShellOpsSmokeTest.RunAsync(args[1..]);
}

var options = SmokeTestOptions.Parse(args);
if (options is null)
{
    return 2;
}

Console.WriteLine("=== PythonApiClient 疎通確認 ===");

JobObjectManager? jobObjectManager = null;
PythonProcessManager? processManager = null;
using var apiClient = new PythonApiClient();

try
{
    int port;
    string token;

    if (options.Attach)
    {
        Console.WriteLine($"[attach] 既存プロセスへ接続します（port={options.Port}）。");
        port = options.Port!.Value;
        token = options.Token!;
    }
    else
    {
        string repoRoot = options.RepoRoot ?? FindRepositoryRoot();
        Console.WriteLine($"[launch] リポジトリルート: {repoRoot}");
        Console.WriteLine($"[launch] 実行ファイル: {options.PythonExecutable}");

        jobObjectManager = new JobObjectManager();
        processManager = PythonProcessManager.CreateForPyService(
            jobObjectManager,
            repoRoot,
            options.PythonExecutable,
            handshakeTimeout: TimeSpan.FromSeconds(10));

        Console.WriteLine("[launch] Pythonプロセスを起動し、起動ハンドシェイク（\"PORT: <number>\"待ち）を開始します...");
        var stopwatch = Stopwatch.StartNew();
        PythonHandshakeResult handshake = await processManager.StartAsync();
        stopwatch.Stop();

        port = handshake.Port;
        token = handshake.Token;
        Console.WriteLine($"[launch] ハンドシェイク完了（{stopwatch.ElapsedMilliseconds}ms）: port={port}");
    }

    apiClient.Configure(port, token);

    Console.WriteLine($"[health] GET {PythonApiClient.HealthEndpointPath} ...");
    bool healthy = await apiClient.HealthCheckAsync();
    Console.WriteLine(healthy ? "[health] OK" : "[health] NG（接続失敗 or タイムアウト or 非2xx応答）");

    var request = new AnalyzeRequest
    {
        FilePath = options.FilePath ?? @"C:\Demo\Inbox\sample.pdf",
        OcrText = null,
        ExtractFields = ["date", "company", "document_type", "category"],
    };

    Console.WriteLine($"[analyze] POST {PythonApiClient.AnalyzeEndpointPath} file_path=\"{request.FilePath}\" ...");
    AnalyzeResponse? response = await apiClient.AnalyzeAsync(request);

    if (response is null)
    {
        Console.WriteLine("[analyze] NG: null が返却されました（接続失敗・タイムアウト・非2xx応答のいずれか）。");
        Console.WriteLine("[analyze] py_service側が AI_IMPLEMENTATION_GUIDE.md §3.1/§3.2（PORT出力形式・ORGANIZER_IPC_TOKEN・");
        Console.WriteLine("           /api/v1/analyze パス・リクエスト/レスポンススキーマ）に未対応の可能性があります。");
        return 1;
    }

    Console.WriteLine("[analyze] OK:");
    Console.WriteLine($"           success    = {response.Success}");
    Console.WriteLine($"           category   = {response.Category}");
    Console.WriteLine($"           confidence = {response.Confidence}");
    if (response.Metadata is { Count: > 0 })
    {
        foreach (var (key, value) in response.Metadata)
        {
            Console.WriteLine($"           metadata[{key}] = {value}");
        }
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}");
    return 1;
}
finally
{
    processManager?.Dispose();
    jobObjectManager?.Dispose();
}

static string FindRepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FileOrganizer.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName
        ?? throw new InvalidOperationException(
            "リポジトリルート（FileOrganizer.slnx）が見つかりませんでした。--repo-root で明示的に指定してください。");
}

internal sealed class SmokeTestOptions
{
    public bool Attach { get; private init; }
    public int? Port { get; private init; }
    public string? Token { get; private init; }
    public string? RepoRoot { get; private init; }
    public string PythonExecutable { get; private init; } = "python";
    public string? FilePath { get; private init; }

    public static SmokeTestOptions? Parse(string[] args)
    {
        bool attach = false;
        int? port = null;
        string? token = null;
        string? repoRoot = null;
        string pythonExecutable = "python";
        string? filePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--attach":
                    attach = true;
                    break;
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i]);
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--repo-root" when i + 1 < args.Length:
                    repoRoot = args[++i];
                    break;
                case "--python" when i + 1 < args.Length:
                    pythonExecutable = args[++i];
                    break;
                case "--file" when i + 1 < args.Length:
                    filePath = args[++i];
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"不明な引数です: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }

        if (attach && (port is null || string.IsNullOrWhiteSpace(token)))
        {
            Console.Error.WriteLine("--attach を指定する場合は --port と --token が必須です。");
            PrintUsage();
            return null;
        }

        return new SmokeTestOptions
        {
            Attach = attach,
            Port = port,
            Token = token,
            RepoRoot = repoRoot,
            PythonExecutable = pythonExecutable,
            FilePath = filePath,
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            使い方:
              dotnet run --project FileOrganizer.Core.SmokeTest -- [--repo-root <path>] [--python <exe>] [--file <path>]
              dotnet run --project FileOrganizer.Core.SmokeTest -- --attach --port <port> --token <token> [--file <path>]
              dotnet run --project FileOrganizer.Core.SmokeTest -- --shell-ops [--drive-a <path>] [--drive-b <path>]
            """);
    }
}

/// <summary>
/// SafeFileOperations（ShellFileOperations = AI_IMPLEMENTATION_GUIDE.md §4.1 + フォールバック層）の疎通確認。
/// 仕様書§3.2「不可逆な物理削除の禁止」「Cross-Volume非同期移動」を実プロセス・実ファイルで確認する。
/// </summary>
internal static class ShellOpsSmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? driveA = null;
        string? driveB = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--drive-a" when i + 1 < args.Length:
                    driveA = args[++i];
                    break;
                case "--drive-b" when i + 1 < args.Length:
                    driveB = args[++i];
                    break;
            }
        }

        Console.WriteLine("=== SafeFileOperations 疎通確認（ShellFileOperations + フォールバック層） ===");

        // 0. このマシンでIFileOperationのCOMアクティブ化が使えるか（使えない場合はSafeFileOperationsが
        //    Microsoft.VisualBasic.FileIO.FileSystemベースのフォールバックへ自動的に切り替える）。
        bool usingShellFileOperations = SafeFileOperations.IsUsingShellFileOperations;
        Console.WriteLine(usingShellFileOperations
            ? "[prereq] IFileOperationが利用可能: ShellFileOperations（AI_IMPLEMENTATION_GUIDE.md §4.1）を使用します。"
            : "[prereq] IFileOperationのCOMアクティブ化が不可: Microsoft.VisualBasic.FileIO.FileSystemベースの" +
              "フォールバックを使用します（詳細はSafeFileOperations.csのremarksを参照）。");

        string workDir = Path.Combine(Path.GetTempPath(), "ShellOpsSmokeTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            bool recycleOk = await RunRecycleBinCheckAsync(workDir);
            bool moveOk = await RunCrossVolumeMoveCheckAsync(workDir, driveA, driveB);
            bool cancelOk = await RunCancellationCheckAsync(workDir);

            Console.WriteLine();
            Console.WriteLine($"[summary] recycle-bin={(recycleOk ? "PASS" : "FAIL")} cross-volume-move={(moveOk ? "PASS" : "FAIL")} cancellation={(cancelOk ? "PASS" : "FAIL")}");
            return recycleOk && moveOk && cancelOk ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // 後始末失敗は無視。
            }
        }
    }

    private static async Task<bool> RunRecycleBinCheckAsync(string workDir)
    {
        Console.WriteLine();
        Console.WriteLine("--- 1) SendToRecycleBinAsync: ごみ箱退避の確認 ---");

        string fileName = $"shellops-{Guid.NewGuid():N}.txt";
        string path = Path.Combine(workDir, fileName);
        File.WriteAllText(path, "shell-ops-smoke-test");
        Console.WriteLine($"[recycle] 作成: {path}");

        bool result = await SafeFileOperations.SendToRecycleBinAsync(path);
        bool sourceGone = !File.Exists(path);
        Console.WriteLine($"[recycle] SendToRecycleBinAsync戻り値={result}, 移動元ファイル消失={sourceGone}");

        if (!result || !sourceGone)
        {
            Console.WriteLine("[recycle] FAIL");
            return false;
        }

        bool foundInBin = RunOnSta(() => TryFindAndPurgeFromRecycleBin(fileName));
        Console.WriteLine(foundInBin
            ? "[recycle] ごみ箱内にアイテムを確認しました（後始末として完全削除済み）。"
            : "[recycle] 警告: ごみ箱内にアイテムを確認できませんでした（手動でごみ箱をご確認ください）。");

        Console.WriteLine(foundInBin ? "[recycle] PASS" : "[recycle] FAIL");
        return foundInBin;
    }

    private static async Task<bool> RunCrossVolumeMoveCheckAsync(string workDir, string? driveAOverride, string? driveBOverride)
    {
        Console.WriteLine();
        Console.WriteLine("--- 2) MoveFileSafelyAsync: Cross-Volume非同期移動 + ディレクトリ自動作成の確認 ---");

        string sourceRoot;
        string destRoot;
        bool isCrossVolume;

        if (driveAOverride is not null && driveBOverride is not null)
        {
            sourceRoot = driveAOverride;
            destRoot = driveBOverride;
            isCrossVolume = true;
        }
        else
        {
            var readyDrives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).ToList();
            if (readyDrives.Count >= 2)
            {
                sourceRoot = readyDrives[0].RootDirectory.FullName;
                destRoot = readyDrives[1].RootDirectory.FullName;
                isCrossVolume = true;
            }
            else
            {
                Console.WriteLine("[move] 警告: 利用可能なドライブが1つしか検出できなかったため、Cross-Volumeではなく同一ボリュームでフォールバック検証します。");
                Console.WriteLine("        （--drive-a/--drive-b で別ボリューム上のパスを指定すると本当のCross-Volume検証ができます）");
                sourceRoot = workDir;
                destRoot = workDir;
                isCrossVolume = false;
            }
        }

        string sourceDir = Path.Combine(sourceRoot.TrimEnd('\\'), "FileOrganizerShellOpsSmokeTest", "src", Guid.NewGuid().ToString("N"));
        // 移動先はネストした未作成ディレクトリを指定し、自動作成を確認する。
        string destDir = Path.Combine(destRoot.TrimEnd('\\'), "FileOrganizerShellOpsSmokeTest", "dest", Guid.NewGuid().ToString("N"), "nested", "subdir");
        Directory.CreateDirectory(sourceDir);

        string fileName = $"movetest-{Guid.NewGuid():N}.txt";
        string sourcePath = Path.Combine(sourceDir, fileName);
        File.WriteAllText(sourcePath, "cross-volume-move-test");

        Console.WriteLine($"[move] source = {sourcePath}");
        Console.WriteLine($"[move] dest   = {destDir}  (isCrossVolume={isCrossVolume})");
        Console.WriteLine($"[move] 移動先ディレクトリの事前存在: {Directory.Exists(destDir)}（falseなら自動作成の検証になる）");

        try
        {
            var callStopwatch = Stopwatch.StartNew();
            Task<bool> moveTask = SafeFileOperations.MoveFileSafelyAsync(sourcePath, destDir);
            callStopwatch.Stop();
            Console.WriteLine($"[move] 呼び出しが制御を返すまで: {callStopwatch.ElapsedMilliseconds}ms（非ブロッキングであれば短時間で戻る）");

            var totalStopwatch = Stopwatch.StartNew();
            bool result = await moveTask;
            totalStopwatch.Stop();
            Console.WriteLine($"[move] 完了まで(await込み): {totalStopwatch.ElapsedMilliseconds}ms, 戻り値={result}");

            bool destDirCreated = Directory.Exists(destDir);
            bool destFileExists = File.Exists(Path.Combine(destDir, fileName));
            bool sourceGone = !File.Exists(sourcePath);
            Console.WriteLine($"[move] 移動先ディレクトリ自動作成={destDirCreated}, 移動先にファイル存在={destFileExists}, 移動元消失={sourceGone}");

            bool pass = result && destDirCreated && destFileExists && sourceGone;
            Console.WriteLine(pass ? "[move] PASS" : "[move] FAIL");
            return pass;
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { /* ignore */ }
            try { Directory.Delete(destDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task<bool> RunCancellationCheckAsync(string workDir)
    {
        Console.WriteLine();
        Console.WriteLine("--- 3) キャンセルトークンでの中断確認 ---");

        string path = Path.Combine(workDir, $"canceltest-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "cancellation-test");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await SafeFileOperations.MoveFileSafelyAsync(path, Path.Combine(workDir, "cancel-dest"), cancellationToken: cts.Token);
            Console.WriteLine("[cancel] FAIL: OperationCanceledExceptionが伝播しませんでした。");
            return false;
        }
        catch (OperationCanceledException ex)
        {
            bool fileUntouched = File.Exists(path);
            Console.WriteLine($"[cancel] {ex.GetType().Name} を捕捉。移動元ファイルは無傷={fileUntouched}");
            Console.WriteLine(fileUntouched ? "[cancel] PASS" : "[cancel] FAIL");
            return fileUntouched;
        }
    }

    /// <summary>Shell.Application COM自動化でごみ箱内アイテムを探し、見つかれば完全削除して後始末する。</summary>
    private static bool TryFindAndPurgeFromRecycleBin(string fileName)
    {
        Type shellAppType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application COMオブジェクトが見つかりません。");
        dynamic shell = Activator.CreateInstance(shellAppType)
            ?? throw new InvalidOperationException("Shell.Applicationのインスタンス化に失敗しました。");

        try
        {
            dynamic recycleBin = shell.Namespace(10); // ssfBITBUCKET
            dynamic items = recycleBin.Items();
            int count = items.Count;

            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        item.InvokeVerb("delete");
                    }
                    catch
                    {
                        // 後始末失敗は無視。
                    }

                    return true;
                }
            }

            return false;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? capturedException = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                capturedException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        capturedException?.Throw();
        return result;
    }
}
