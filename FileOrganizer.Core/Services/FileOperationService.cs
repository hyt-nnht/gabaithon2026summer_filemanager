using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Core.Utils;
using FileOrganizer.Core.Win32;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Services;

/// <summary>
/// <see cref="IFileOperationService"/>の実装。内部で<see cref="SafeFileOperations"/>
/// （AI_IMPLEMENTATION_GUIDE.md §4.1 <c>ShellFileOperations</c>のCOM<c>IFileOperation</c>実装を優先し、
/// 利用不可環境では自動フォールバックする層。Phase0成果物）を利用する。
/// 仕様書§6「同名衝突の防止」「監視ループ防止」を満たす。
/// </summary>
/// <remarks>
/// <para>
/// <b>同名衝突の防止</b>: 移動・コピー時、移動先に同名ファイル/フォルダが既に存在する場合は
/// 既定で上書きを行わない。挙動は呼び出しごとの<see cref="ConflictPolicy"/>引数（Move/Copy）で
/// 制御する。<see cref="ConflictPolicy.AutoRename"/>は連番（<c>_1</c>, <c>_2</c>, ...）を付与した
/// 空いている名前を探して続行し、<see cref="ConflictPolicy.Skip"/>は何も行わず
/// <see cref="OperationResult.WasSkippedDueToConflict"/>=trueで成功扱いを返す。
/// <see cref="ConflictPolicy.PromptUser"/>は本サービス（UIを持たないバックエンド層）では
/// 確認ダイアログを出せないため、操作を実行せず「確認が必要」を示す結果を返す
/// （呼び出し側UIが確認後、改めてAutoRename/Skipで呼び直す想定）。
/// <see cref="RenameAsync"/>は<see cref="IFileOperationService"/>のシグネチャ上ポリシーを
/// 受け取れないため、コンストラクタ引数（<c>AppSettings</c>相当の既定値）で制御する。
/// </para>
/// <para>
/// <b>ファイル名サニタイズ</b>: <see cref="RenameAsync"/>はシェル操作を呼ぶ直前に必ず
/// <see cref="PathSanitizer.SanitizeFileName"/>を通す。
/// </para>
/// <para>
/// <b>監視ループ防止</b>: Move/Copy/Renameの各操作は、実際にファイルを動かす直前に
/// <see cref="IWatchSuppressor"/>（Watcher連携用インターフェース、本ファイルと合わせて提案）へ
/// 冪等性トークン（GUID）付きで移動/コピー/リネーム先パスを通知する。Watcher側はこの通知を基に
/// 該当パスの後続イベントを一定期間抑止し、自アプリの操作が監視対象フォルダ内で再度検知されて
/// 無限ループ的に処理される事故を防ぐ。ゴミ箱送り（<see cref="RecycleAsync"/>）は監視対象フォルダ内へ
/// 新たなパスを生まないため対象外。
/// </para>
/// </remarks>
public sealed class FileOperationService : IFileOperationService
{
    /// <summary>
    /// Watcherへの抑止要求の既定期間。パス単位デバウンス（既定300ms）+ 集約ポーリングワーカーによる
    /// 2回一致確認（既定750ms×2〜3サイクル）を安全に包含できる長さとして30秒を既定値とする。
    /// </summary>
    public static readonly TimeSpan DefaultWatchSuppressDuration = TimeSpan.FromSeconds(30);

    private readonly IWatchSuppressor? _watchSuppressor;
    private readonly TimeSpan _watchSuppressDuration;
    private readonly ConflictPolicy _renameConflictPolicy;

    /// <param name="watchSuppressor">
    /// Watcher連携インターフェース。省略（null）の場合、監視ループ防止の通知は行われない
    /// （Watcherが未起動のコンテキスト、単体実行ツール等での利用を想定）。
    /// </param>
    /// <param name="watchSuppressDuration">Watcherへの抑止要求期間。省略時は<see cref="DefaultWatchSuppressDuration"/>。</param>
    /// <param name="renameConflictPolicy">
    /// <see cref="RenameAsync"/>で使う同名衝突時の既定ポリシー（<c>AppSettings</c>相当）。
    /// <see cref="IFileOperationService.RenameAsync"/>のシグネチャに<see cref="ConflictPolicy"/>引数が
    /// 無いため、呼び出しごとではなくサービス構築時に決める。既定は<see cref="ConflictPolicy.AutoRename"/>。
    /// </param>
    public FileOperationService(
        IWatchSuppressor? watchSuppressor = null,
        TimeSpan? watchSuppressDuration = null,
        ConflictPolicy renameConflictPolicy = ConflictPolicy.AutoRename)
    {
        _watchSuppressor = watchSuppressor;
        _watchSuppressDuration = watchSuppressDuration ?? DefaultWatchSuppressDuration;
        _renameConflictPolicy = renameConflictPolicy;
    }

    public Task<OperationResult> MoveAsync(string sourcePath, string destinationDirectory, ConflictPolicy policy, CancellationToken ct = default)
        => MoveOrCopyAsync(sourcePath, destinationDirectory, policy, isCopy: false, ct);

    public Task<OperationResult> CopyAsync(string sourcePath, string destinationDirectory, ConflictPolicy policy, CancellationToken ct = default)
        => MoveOrCopyAsync(sourcePath, destinationDirectory, policy, isCopy: true, ct);

    public async Task<OperationResult> RenameAsync(string sourcePath, string newFileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFileName);

        if (!File.Exists(sourcePath))
        {
            return Failure($"リネーム対象ファイルが見つかりません: {sourcePath}");
        }

        string? directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
        {
            return Failure($"移動元の親フォルダを特定できません: {sourcePath}");
        }

        // 1-1: リネーム実行直前に必ずサニタイズ（禁止文字・予約名・末尾ドット/空白除去）。
        string sanitizedFileName = PathSanitizer.SanitizeFileName(newFileName);
        string currentFileName = Path.GetFileName(sourcePath);

        if (string.Equals(sanitizedFileName, currentFileName, StringComparison.Ordinal))
        {
            // サニタイズ後、現在名と完全一致（実質的な変更なし）→ 何もせず成功扱い。
            return new OperationResult { Success = true, FinalPath = sourcePath };
        }

        string candidatePath = Path.Combine(directory, sanitizedFileName);
        bool isCaseOnlyRenameOfSelf =
            string.Equals(candidatePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidatePath, sourcePath, StringComparison.Ordinal);

        ConflictResolution resolution = isCaseOnlyRenameOfSelf
            // 大文字小文字のみの変更は「自分自身」であり同名衝突ではない（Windowsは大文字小文字を
            // 区別しないため、素朴なFile.Exists判定では自分自身を衝突として誤検知してしまう）。
            ? new ConflictResolution(ConflictResolutionOutcome.NoConflict, sanitizedFileName)
            : ConflictResolver.Resolve(directory, sanitizedFileName, _renameConflictPolicy);

        switch (resolution.Outcome)
        {
            case ConflictResolutionOutcome.Skip:
                return SkippedResult();
            case ConflictResolutionOutcome.PromptRequired:
                return PromptRequiredResult(candidatePath);
        }

        string finalFileName = resolution.ResolvedFileName!;
        string destinationPath = Path.Combine(directory, finalFileName);

        NotifyWatcher(destinationPath);

        bool ok = await SafeFileOperations.RenameFileSafelyAsync(sourcePath, finalFileName, cancellationToken: ct).ConfigureAwait(false);

        return ok
            ? new OperationResult { Success = true, FinalPath = destinationPath }
            : Failure($"リネームに失敗しました: {sourcePath} -> {finalFileName}");
    }

    public async Task<OperationResult> RecycleAsync(string sourcePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return Failure($"ゴミ箱送り対象が見つかりません: {sourcePath}");
        }

        // ゴミ箱送りは監視対象フォルダ内へ新たなパスを生まないため、Watcherへの抑止通知は不要。
        bool ok = await SafeFileOperations.SendToRecycleBinAsync(sourcePath, cancellationToken: ct).ConfigureAwait(false);

        return ok
            ? new OperationResult { Success = true, FinalPath = null }
            : Failure($"ゴミ箱への移動に失敗しました: {sourcePath}");
    }

    // --- Move/Copy共通処理 ---------------------------------------------------------------

    private async Task<OperationResult> MoveOrCopyAsync(
        string sourcePath,
        string destinationDirectory,
        ConflictPolicy policy,
        bool isCopy,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string verb = isCopy ? "コピー" : "移動";

        if (!File.Exists(sourcePath))
        {
            return Failure($"{verb}元ファイルが見つかりません: {sourcePath}");
        }

        string desiredFileName = Path.GetFileName(sourcePath);
        var resolution = ConflictResolver.Resolve(destinationDirectory, desiredFileName, policy);

        switch (resolution.Outcome)
        {
            case ConflictResolutionOutcome.Skip:
                return SkippedResult();
            case ConflictResolutionOutcome.PromptRequired:
                return PromptRequiredResult(Path.Combine(destinationDirectory, desiredFileName));
        }

        string finalFileName = resolution.ResolvedFileName!;
        string destinationPath = Path.Combine(destinationDirectory, finalFileName);

        NotifyWatcher(destinationPath);

        bool ok = isCopy
            ? await SafeFileOperations.CopyFileSafelyAsync(sourcePath, destinationDirectory, finalFileName, cancellationToken: ct).ConfigureAwait(false)
            : await SafeFileOperations.MoveFileSafelyAsync(sourcePath, destinationDirectory, finalFileName, cancellationToken: ct).ConfigureAwait(false);

        return ok
            ? new OperationResult { Success = true, FinalPath = destinationPath }
            : Failure($"{verb}に失敗しました: {sourcePath} -> {destinationPath}");
    }

    /// <summary>移動/コピー/リネーム先パスを、冪等性トークンを発行してWatcher側へ通知する。</summary>
    private void NotifyWatcher(string destinationPath)
    {
        string idempotencyToken = Guid.NewGuid().ToString("N");
        // シェル操作より先に抑止登録を完了させる（操作完了後にイベントが飛んでくるより前に済ませ、
        // 抑止未登録の一瞬をFileSystemWatcherのイベントに拾われる取りこぼしを防ぐ）。
        _watchSuppressor?.SuppressPath(destinationPath, _watchSuppressDuration, idempotencyToken);
    }

    // --- 結果生成ヘルパー -------------------------------------------------------------------

    private static OperationResult Failure(string message) => new()
    {
        Success = false,
        ErrorMessage = message,
    };

    private static OperationResult SkippedResult() => new()
    {
        Success = true,
        WasSkippedDueToConflict = true,
    };

    private static OperationResult PromptRequiredResult(string conflictingPath) => new()
    {
        Success = false,
        WasSkippedDueToConflict = true,
        ErrorMessage = $"同名のファイル/フォルダが既に存在するため、ユーザーへの確認が必要です（PromptUserポリシー）: {conflictingPath}",
    };
}
