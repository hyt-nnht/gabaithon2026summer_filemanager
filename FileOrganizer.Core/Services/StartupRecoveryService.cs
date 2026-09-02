using System.Threading.Tasks;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Services;

/// <summary>
/// AI_IMPLEMENTATION_GUIDE.md §6準拠の起動時リカバリロジック。
/// アプリクラッシュや強制終了時に <c>Executing</c>／<c>Undoing</c> のまま中途半端に残った
/// <see cref="HistoryRecord"/> を、実ファイルの現在の状態と照合して自動整合復旧する。
/// 仕様書§7.2-2「耐障害性」（強制終了後もファイル消失・不整合履歴なく復旧できること）を満たす。
/// </summary>
public class StartupRecoveryService
{
    private readonly IHistoryRepository _historyRepository;
    private readonly IFileSystem _fileSystem;

    /// <param name="historyRepository">1-3で実装した<see cref="IHistoryRepository"/>（SQLite実装等）。</param>
    /// <param name="fileSystem">
    /// ファイル実在確認の抽象化。省略時は<see cref="PhysicalFileSystem"/>（実際の<c>File.Exists</c>）を使用する。
    /// 単体テストではフェイク実装を注入して<c>File.Exists</c>相当の結果をモック化できる。
    /// </param>
    public StartupRecoveryService(IHistoryRepository historyRepository, IFileSystem? fileSystem = null)
    {
        _historyRepository = historyRepository;
        _fileSystem = fileSystem ?? new PhysicalFileSystem();
    }

    public async Task PerformStartupRecoveryAsync()
    {
        // 1. Executing（通常実行中）のまま中断されたレコードの復旧
        var executingRecords = await _historyRepository.GetRecordsByStateAsync(OperationState.Executing);
        foreach (var record in executingRecords)
        {
            bool sourceExists = _fileSystem.FileExists(record.SourcePath);
            bool destExists = !string.IsNullOrEmpty(record.DestinationPath) && _fileSystem.FileExists(record.DestinationPath);

            switch (record.OpType)
            {
                case OperationType.Move:
                case OperationType.Rename:
                    if (destExists && !sourceExists)
                    {
                        // 移動先が存在し元が存在しない ➔ 完了とみなす
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Completed);
                    }
                    else
                    {
                        // それ以外は失敗として記録
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Failed, "クラッシュによる中断（未完了）");
                    }
                    break;

                case OperationType.Copy:
                    if (destExists)
                    {
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Completed);
                    }
                    else
                    {
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Failed, "クラッシュによる中断（コピー未完了）");
                    }
                    break;

                case OperationType.Recycle:
                    if (!sourceExists)
                    {
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Completed);
                    }
                    else
                    {
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Failed, "クラッシュによる中断（ゴミ箱移動未完了）");
                    }
                    break;
            }
        }

        // 2. Undoing（ロールバック復元中）のまま中断されたレコードの復旧
        var undoingRecords = await _historyRepository.GetRecordsByStateAsync(OperationState.Undoing);
        foreach (var record in undoingRecords)
        {
            bool sourceExists = _fileSystem.FileExists(record.SourcePath);
            bool destExists = !string.IsNullOrEmpty(record.DestinationPath) && _fileSystem.FileExists(record.DestinationPath);

            switch (record.OpType)
            {
                case OperationType.Move:
                case OperationType.Rename:
                    if (sourceExists && !destExists)
                    {
                        // 元パスにファイルが戻っており、移動先には残っていない ➔ Undo完了とみなす
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Undone);
                    }
                    else
                    {
                        // 元に戻りきっていない、または両方に存在する等 ➔ UndoFailed
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.UndoFailed, "Undo処理中のクラッシュによる中断");
                    }
                    break;

                case OperationType.Copy:
                    // コピーUndo（作成先ファイルの削除）
                    if (!destExists)
                    {
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.Undone);
                    }
                    else
                    {
                        await _historyRepository.UpdateStateAsync(record.Id, OperationState.UndoFailed, "Undo処理中のクラッシュによるコピー先残存");
                    }
                    break;

                default:
                    await _historyRepository.UpdateStateAsync(record.Id, OperationState.UndoFailed, "未対応のUndo状態");
                    break;
            }
        }
    }
}
