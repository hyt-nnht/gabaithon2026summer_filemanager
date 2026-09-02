using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Core.Utils;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>
/// <see cref="IUndoManager"/>の実装。仕様書§3.2「操作別Undo(復元)仕様と競合制御」に従い、
/// 「履歴と現在のファイルが同一であること」を1-2 <see cref="HashHelper"/>の軽量ハッシュで検証してから
/// 1-8 <see cref="IFileOperationService"/>で実際の復元操作を行う。状態は<c>Completed</c>から
/// <c>Undoing</c>を経て<c>Undone</c>/<c>UndoFailed</c>へ、1-3 <see cref="IHistoryRepository"/>で更新する。
/// </summary>
/// <remarks>
/// <para>
/// <b>操作別のUndo仕様</b>:
/// </para>
/// <list type="bullet">
/// <item><description><b>移動(Move)</b>: 移動先から元パスへ戻す。元パスに別ファイルが存在する場合、
/// 自動上書きは禁止し、<see cref="MoveRestoreConflictPolicy"/>に応じて別名復元
/// （<see cref="ConflictPolicy.AutoRename"/>）または確認要求（既定）を行う。</description></item>
/// <item><description><b>リネーム(Rename)</b>: 現在の名称から元の名称へ戻す。仕様書は移動と異なり
/// 別名復元の選択肢を明示していないため、元の名前が別ファイルで使用中の場合は常に確認要求とし、
/// 自動上書き・自動別名付与のいずれも行わない。</description></item>
/// <item><description><b>コピー(Copy)</b>: 作成されたコピー先ファイルをゴミ箱へ送る
/// （元ファイルはコピーでは移動していないため何もしない）。</description></item>
/// <item><description><b>ゴミ箱送り(Recycle)</b>: MVPではアプリ内自動Undo対象外。
/// Windowsのゴミ箱からの手動復元を案内するメッセージ付きで<see cref="UndoOutcome.Failed"/>を返す。</description></item>
/// </list>
/// <para>
/// <b>ハッシュ検証</b>: いずれの操作種別でも、実操作を行う前に現在のファイルの軽量ハッシュ
/// （<see cref="HashHelper.ComputeLightweightHash"/>）と<see cref="HistoryRecord.LightweightHash"/>
/// （元操作の直前に記録済み）を比較する。不一致（操作後にユーザーが内容を変更した可能性）の場合は
/// 自動上書きせず<see cref="UndoOutcome.RequiresConfirmation"/>を返し、DBの状態は変更しない
/// （<c>Completed</c>のまま。呼び出し側が別途、確認の上で解決することを想定）。
/// </para>
/// </remarks>
public sealed class UndoManager : IUndoManager
{
    private readonly IHistoryRepository _historyRepository;
    private readonly IFileOperationService _fileOperationService;

    /// <summary>
    /// 移動(Move)Undo時、復元先（元パス）に別ファイルが存在する場合の解決方針。
    /// <see cref="ConflictPolicy.AutoRename"/>なら連番付与で別名復元し、それ以外
    /// （<see cref="ConflictPolicy.PromptUser"/>・<see cref="ConflictPolicy.Skip"/>）は
    /// 常に<see cref="UndoOutcome.RequiresConfirmation"/>を返す。既定は<see cref="ConflictPolicy.PromptUser"/>
    /// （仕様書§3.2の「別名復元または確認ダイアログを表示」のうち、より安全な確認ダイアログ側を既定とする）。
    /// </summary>
    public ConflictPolicy MoveRestoreConflictPolicy { get; }

    public UndoManager(
        IHistoryRepository historyRepository,
        IFileOperationService fileOperationService,
        ConflictPolicy moveRestoreConflictPolicy = ConflictPolicy.PromptUser)
    {
        _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
        _fileOperationService = fileOperationService ?? throw new ArgumentNullException(nameof(fileOperationService));
        MoveRestoreConflictPolicy = moveRestoreConflictPolicy;
    }

    public async Task<UndoResult> UndoAsync(long historyRecordId, CancellationToken ct = default)
    {
        var record = await _historyRepository.GetByIdAsync(historyRecordId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return Failed($"履歴レコードが見つかりません (id={historyRecordId})。");
        }
        return await UndoCoreAsync(record, ct).ConfigureAwait(false);
    }

    public async Task<UndoResult> UndoAsync(string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var record = await _historyRepository.GetByOperationIdAsync(operationId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return Failed($"履歴レコードが見つかりません (operationId={operationId})。");
        }
        return await UndoCoreAsync(record, ct).ConfigureAwait(false);
    }

    private async Task<UndoResult> UndoCoreAsync(HistoryRecord record, CancellationToken ct)
    {
        if (record.State != OperationState.Completed)
        {
            return Failed($"完了済み（Completed）の操作のみUndoできます（id={record.Id}の現在の状態: {record.State}）。");
        }

        if (record.OpType == OperationType.Recycle)
        {
            // 仕様書§3.2: ゴミ箱送りはMVPではアプリ内自動Undo対象外
            // （同名・同一パスの項目を完全特定できない場合の誤復元を防止するため）。
            return Failed("ゴミ箱送り操作はアプリからは復元できません。Windowsのゴミ箱から手動で復元してください。");
        }

        return record.OpType switch
        {
            OperationType.Move => await UndoMoveAsync(record, ct).ConfigureAwait(false),
            OperationType.Rename => await UndoRenameAsync(record, ct).ConfigureAwait(false),
            OperationType.Copy => await UndoCopyAsync(record, ct).ConfigureAwait(false),
            _ => Failed($"未対応のOperationTypeです: {record.OpType}"),
        };
    }

    // --- 移動(Move)のUndo -----------------------------------------------------------------

    private async Task<UndoResult> UndoMoveAsync(HistoryRecord record, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(record.DestinationPath))
        {
            return Failed("履歴データが不整合です（移動先パスが記録されていません）。");
        }

        var hashCheck = VerifyCurrentFileHash(record.DestinationPath, record.LightweightHash);
        if (hashCheck != null) return hashCheck;

        string? restoreDirectory = Path.GetDirectoryName(record.SourcePath);
        if (string.IsNullOrEmpty(restoreDirectory))
        {
            return Failed("履歴データが不整合です（復元先ディレクトリを特定できません）。");
        }

        bool conflict = File.Exists(record.SourcePath) || Directory.Exists(record.SourcePath);
        if (conflict && MoveRestoreConflictPolicy != ConflictPolicy.AutoRename)
        {
            // 仕様書§3.2「移動先から元パスへ戻す。元パスに別ファイルが存在する場合、
            // 自動上書きを禁止し、別名復元または確認ダイアログを表示」の後者。
            return RequiresConfirmation($"復元先に別のファイルが既に存在します: {record.SourcePath}");
        }

        // 衝突が無い、またはAutoRenameで別名復元してよい場合のみ実行する
        // （FileOperationService.MoveAsyncの衝突解決ロジックをそのまま利用）。
        return await ExecuteUndoAsync(
            record,
            () => _fileOperationService.MoveAsync(record.DestinationPath!, restoreDirectory, MoveRestoreConflictPolicy, ct),
            ct).ConfigureAwait(false);
    }

    // --- リネーム(Rename)のUndo -------------------------------------------------------------

    private async Task<UndoResult> UndoRenameAsync(HistoryRecord record, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(record.DestinationPath))
        {
            return Failed("履歴データが不整合です（リネーム後パスが記録されていません）。");
        }

        var hashCheck = VerifyCurrentFileHash(record.DestinationPath, record.LightweightHash);
        if (hashCheck != null) return hashCheck;

        string originalFileName = Path.GetFileName(record.SourcePath);

        // 仕様書§3.2「元のファイル名が別ファイルで使用中の場合、自動上書きを禁止」。
        // Moveと異なり別名復元の選択肢が明示されていないため、衝突時は常に確認要求とする。
        bool conflict = File.Exists(record.SourcePath) || Directory.Exists(record.SourcePath);
        if (conflict)
        {
            return RequiresConfirmation($"復元先の名前が既に別のファイルで使用されています: {record.SourcePath}");
        }

        return await ExecuteUndoAsync(
            record,
            () => _fileOperationService.RenameAsync(record.DestinationPath!, originalFileName, ct),
            ct).ConfigureAwait(false);
    }

    // --- コピー(Copy)のUndo -----------------------------------------------------------------

    private async Task<UndoResult> UndoCopyAsync(HistoryRecord record, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(record.DestinationPath))
        {
            return Failed("履歴データが不整合です（コピー先パスが記録されていません）。");
        }

        // 仕様書§3.2「コピー先がユーザーにより更新されている場合、確認ダイアログを表示」。
        var hashCheck = VerifyCurrentFileHash(record.DestinationPath, record.LightweightHash);
        if (hashCheck != null) return hashCheck;

        // 元ファイル（SourcePath）はコピーでは変更されていないため、コピー先をゴミ箱へ送るのみ。
        return await ExecuteUndoAsync(
            record,
            () => _fileOperationService.RecycleAsync(record.DestinationPath!, ct),
            ct).ConfigureAwait(false);
    }

    // --- 共通処理 ---------------------------------------------------------------------------

    /// <summary>
    /// 現在のファイルの軽量ハッシュを記録済みハッシュと比較する。
    /// 一致すればnull（続行してよい）、ファイル消失やハッシュ不一致であれば結果を返す。
    /// </summary>
    private static UndoResult? VerifyCurrentFileHash(string currentPath, string recordedHash)
    {
        if (!File.Exists(currentPath))
        {
            return Failed($"元操作の対象ファイルが見つかりません: {currentPath}");
        }

        string currentHash = HashHelper.ComputeLightweightHash(currentPath);
        if (!string.Equals(currentHash, recordedHash, StringComparison.Ordinal))
        {
            return RequiresConfirmation($"ファイルが操作後に変更されている可能性があります（ハッシュ不一致）: {currentPath}");
        }

        return null;
    }

    /// <summary>
    /// 実際のUndo操作（<paramref name="action"/>）を、<c>Undoing</c>→<c>Undone</c>/<c>UndoFailed</c>の
    /// 状態遷移を伴って実行する共通処理。
    /// </summary>
    private async Task<UndoResult> ExecuteUndoAsync(HistoryRecord record, Func<Task<OperationResult>> action, CancellationToken ct)
    {
        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Undoing, ct: ct).ConfigureAwait(false);

        OperationResult opResult;
        try
        {
            opResult = await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _historyRepository.UpdateStateAsync(record.Id, OperationState.UndoFailed, ex.Message, ct).ConfigureAwait(false);
            return Failed(ex.Message);
        }

        if (opResult.Success && !opResult.WasSkippedDueToConflict)
        {
            await _historyRepository.UpdateStateAsync(record.Id, OperationState.Undone, ct: ct).ConfigureAwait(false);
            return new UndoResult { Outcome = UndoOutcome.Success, Message = null };
        }

        // 事前チェックにより通常はここへ到達しないが、レース条件（チェック後に他プロセスが
        // 復元先へファイルを作成した等）に備えた防御的処理。
        string message = opResult.ErrorMessage ?? "不明なエラーによりUndoに失敗しました。";
        await _historyRepository.UpdateStateAsync(record.Id, OperationState.UndoFailed, message, ct).ConfigureAwait(false);
        return Failed(message);
    }

    private static UndoResult Failed(string message) => new() { Outcome = UndoOutcome.Failed, Message = message };

    private static UndoResult RequiresConfirmation(string message) => new() { Outcome = UndoOutcome.RequiresConfirmation, Message = message };
}
