using System.Diagnostics;
using FileOrganizer.Core.Database;
using FileOrganizer.Core.Services;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Services;

/// <summary>
/// 仕様書§7.2-2「耐障害性」（移動中やDB書き込み中にプロセスが強制終了しても、次回起動時に
/// ファイル消失や不整合履歴が発生せず復旧できること）のEnd-to-End検証。
/// </summary>
/// <remarks>
/// <see cref="StartupRecoveryServiceTests"/>は<see cref="FakeFileSystem"/>で「ファイルが存在する/しない」
/// という状態遷移の分岐網羅を検証しているのに対し、本クラスは以下すべてを実物で再現する。
/// <list type="bullet">
/// <item><description>実子プロセス（<c>mock_crash_move.ps1</c>）による実ファイル移動</description></item>
/// <item><description><see cref="Process.Kill()"/>による本当の強制終了（正常終了ではない）</description></item>
/// <item><description>実SQLiteデータベース（<see cref="SqliteHistoryRepository"/>）</description></item>
/// <item><description>実ファイルシステム（<see cref="PhysicalFileSystem"/>、フェイクではない）</description></item>
/// </list>
/// 「強制終了→（アプリ）再起動時のStartupRecoveryService動作」を、実際のOSプロセス終了シグナルで
/// 再現して検証する統合テスト。
/// </remarks>
public class StartupRecoveryServiceForcedTerminationTests : IDisposable
{
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "mock_crash_move.ps1");

    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "FileOrganizerTests", "StartupRecoveryForcedTermination", Guid.NewGuid().ToString("N"));

    public StartupRecoveryServiceForcedTerminationTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
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

    [Fact]
    public async Task 移動完了直後に強制終了しても再起動時にCompletedへ復旧しファイルは移動先に残る()
    {
        // --- 準備: 実ファイル・実DB（2フェーズ状態管理のPlanned→Executing）---
        string sourcePath = Path.Combine(_workDir, "sample.pdf");
        string destPath = Path.Combine(_workDir, "organized", "sample.pdf");
        string markerPath = Path.Combine(_workDir, "moved.marker");
        await File.WriteAllTextAsync(sourcePath, "dummy content for forced-termination integration test");

        string dbPath = Path.Combine(_workDir, "history.db");
        string connectionString = DatabaseInitializer.BuildConnectionString(dbPath);
        await new DatabaseInitializer(connectionString).InitializeAsync();
        IHistoryRepository repository = new SqliteHistoryRepository(connectionString);

        var record = new HistoryRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OpType = OperationType.Move,
            SourcePath = sourcePath,
            DestinationPath = destPath,
            FileSizeBytes = new FileInfo(sourcePath).Length,
            FileLastModifiedUtc = DateTime.UtcNow,
            LightweightHash = "HASH",
            State = OperationState.Planned,
        };
        long recordId = await repository.InsertAsync(record);
        // CLAUDE.md「2フェーズ状態管理」: 操作着手前に必ずExecutingへ記録してから実操作を行う。
        await repository.UpdateStateAsync(recordId, OperationState.Executing);

        // --- 実行: 実子プロセスに実ファイル移動をさせ、移動完了の直後（DB更新前）に強制終了させる ---
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath,
                    "-SourcePath", sourcePath, "-DestPath", destPath, "-MarkerPath", markerPath,
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();

        try
        {
            bool moved = SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(15));
            Assert.True(moved, "子プロセスによる実ファイル移動が既定時間内に完了しませんでした。");

            // ここが「強制終了」そのもの: 正常終了(Exit)ではなくKillによる強制シグナル。
            // 子プロセスはこの時点でDB更新（Executing→Completed）をまだ行っていない。
            process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)), "強制終了後もプロセスが終了しませんでした。");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        // 強制終了直後のDB状態を確認: 実ファイルは既に移動済みだが、DBはExecutingのまま（不整合状態）。
        HistoryRecord? crashedRecord = await repository.GetByIdAsync(recordId);
        Assert.Equal(OperationState.Executing, crashedRecord!.State);
        Assert.True(File.Exists(destPath), "前提: 強制終了前に実ファイル移動は完了しているはず。");
        Assert.False(File.Exists(sourcePath), "前提: 移動元には残っていないはず。");

        // --- 「アプリ再起動」を模して、新しいStartupRecoveryServiceインスタンスで復旧を実行 ---
        // 実ファイルシステム（PhysicalFileSystem、フェイクではない）を使う点が本テストの要。
        var recoveryService = new StartupRecoveryService(repository, new PhysicalFileSystem());
        await recoveryService.PerformStartupRecoveryAsync();

        // --- 検証: 仕様書§7.2-2「ファイル消失や不整合履歴が発生せず復旧できる」---
        HistoryRecord? recoveredRecord = await repository.GetByIdAsync(recordId);
        Assert.Equal(OperationState.Completed, recoveredRecord!.State);
        Assert.Null(recoveredRecord.ErrorMessage);
        Assert.True(File.Exists(destPath), "復旧後もファイルは移動先に存在し続けること（消失していない）。");
        Assert.False(File.Exists(sourcePath), "復旧後も移動元にファイルが復活していないこと。");
    }

    [Fact]
    public async Task ファイル移動前に強制終了した場合は再起動時にFailedへ復旧し元ファイルは無傷で残る()
    {
        // 「移動前」の強制終了は、子プロセスを起動せずシミュレートする
        // （Executing記録のみ行い、実操作が一切走っていない状態を再現する）。
        string sourcePath = Path.Combine(_workDir, "untouched.pdf");
        string destPath = Path.Combine(_workDir, "organized", "untouched.pdf");
        await File.WriteAllTextAsync(sourcePath, "should remain untouched");

        string connectionString = DatabaseInitializer.BuildConnectionString(Path.Combine(_workDir, "history2.db"));
        await new DatabaseInitializer(connectionString).InitializeAsync();
        IHistoryRepository repository = new SqliteHistoryRepository(connectionString);

        long recordId = await repository.InsertAsync(new HistoryRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OpType = OperationType.Move,
            SourcePath = sourcePath,
            DestinationPath = destPath,
            FileSizeBytes = new FileInfo(sourcePath).Length,
            FileLastModifiedUtc = DateTime.UtcNow,
            LightweightHash = "HASH",
            State = OperationState.Planned,
        });
        await repository.UpdateStateAsync(recordId, OperationState.Executing);
        // ここで（実際には）プロセスが強制終了した、という想定。実操作は一切行われていない。

        var recoveryService = new StartupRecoveryService(repository, new PhysicalFileSystem());
        await recoveryService.PerformStartupRecoveryAsync();

        HistoryRecord? recoveredRecord = await repository.GetByIdAsync(recordId);
        Assert.Equal(OperationState.Failed, recoveredRecord!.State);
        Assert.True(File.Exists(sourcePath), "未着手のまま中断した場合、元ファイルは無傷で残ること（消失していない）。");
        Assert.False(File.Exists(destPath));
    }
}
