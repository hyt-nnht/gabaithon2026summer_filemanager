using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Core.Utils;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>1回の<see cref="ProcessingCoordinator.ProcessAsync"/>呼び出しが完了した際の結果通知（観測・ログ用）。</summary>
public sealed class ProcessingCompletedEventArgs : EventArgs
{
    public string SourceFullPath { get; }
    public IReadOnlyList<HistoryRecord> Records { get; }

    public ProcessingCompletedEventArgs(string sourceFullPath, IReadOnlyList<HistoryRecord> records)
    {
        SourceFullPath = sourceFullPath;
        Records = records;
    }
}

/// <summary>
/// 仕様書§3.3「実行時フロー」（実行前にDBへ<c>Planned</c>を保存 → 操作直前に<c>Executing</c> →
/// 操作成功確認後に<c>Completed</c>へ更新。失敗時は<c>Failed</c>）を実装するパイプライン統合クラス。
/// </summary>
/// <remarks>
/// <para>
/// <b>パイプライン</b>: 1-5 <see cref="FileStabilityDetector"/>の安定通知（<see cref="FileStableEventArgs"/>）
/// を受け取り → 1-7 <see cref="IRuleEngine"/>（<see cref="RuleEvaluator"/>）で評価 →
/// 一致した各ルールの各<see cref="RuleAction"/>について、1-3 <see cref="IHistoryRepository"/>
/// （<see cref="Database.SqliteHistoryRepository"/>）へ<c>Planned</c>状態で事前記録 →
/// <c>Executing</c>へ更新 → 1-8 <see cref="IFileOperationService"/>
/// （<see cref="Services.FileOperationService"/>）で実操作を実行 →
/// 結果に応じて<c>Completed</c>/<c>Failed</c>へ更新、という一連の流れを担う。
/// </para>
/// <para>
/// <b>複数ルール一致時</b>: <c>AppSettings.ApplyAllMatchingRules</c>が<c>false</c>（既定）なら
/// <see cref="RuleEvaluationResult.MatchedRule"/>（優先順位最上位1件）のみ、<c>true</c>なら
/// <see cref="RuleEvaluationResult.AllMatchedRules"/>（優先順位順）すべてを対象にする
/// （評価順序自体は<see cref="RuleEvaluator"/>が仕様書§6に従って決定済み）。
/// </para>
/// <para>
/// <b>1ルール内の複数アクション</b>: <see cref="RuleModel.Actions"/>は順に実行する。
/// 現在の対象パス（<c>currentPath</c>）はMove/Renameで移動先へ更新され、以降のアクション・ルールは
/// その新しいパスに対して実行される（Copyは元ファイルを維持するため対象パスを変えない）。
/// いずれかのアクションが失敗（<see cref="OperationResult.Success"/>=false）した場合、
/// ファイルの所在が不確実になるため、後続のアクション・ルールは実行せずそこで打ち切る。
/// 同名衝突による意図的なスキップ（<see cref="ConflictPolicy.Skip"/>）は失敗ではないため、
/// 元のパスのまま後続アクションへ継続する。
/// </para>
/// <para>
/// <b>OCR/AI連携（Phase2）</b>: <see cref="IOcrService"/>・<see cref="IPythonApiClient"/>は
/// コンストラクタで受け取り呼び出し口（<see cref="EnrichWithAiMetadataIfNeededAsync"/>）を
/// 用意するのみで、実際の呼び出しはPhase2で実装する（<see cref="RuleEvaluator"/>側も
/// <c>ocr_contains</c>/<c>ai_category</c>条件は<see cref="FileMetadata.OcrText"/>/
/// <see cref="FileMetadata.AiCategory"/>が未設定の間は常に不一致として扱う設計のため、
/// 現時点で呼び出さなくてもルール評価の正しさに影響しない）。
/// </para>
/// </remarks>
public sealed class ProcessingCoordinator
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IHistoryRepository _historyRepository;
    private readonly IFileOperationService _fileOperationService;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOcrService? _ocrService;
    private readonly IPythonApiClient? _pythonApiClient;
    private readonly ConflictPolicy _defaultConflictPolicy;

    /// <summary>1件の安定ファイルに対する処理が完了するたびに発火する（観測・ログ用、任意購読）。</summary>
    public event EventHandler<ProcessingCompletedEventArgs>? ProcessingCompleted;

    /// <param name="ruleEngine">1-7 ルール評価エンジン（<see cref="RuleEvaluator"/>）。</param>
    /// <param name="historyRepository">1-3 2フェーズ状態管理リポジトリ（<see cref="Database.SqliteHistoryRepository"/>）。</param>
    /// <param name="fileOperationService">1-8 実ファイル操作サービス（<see cref="Services.FileOperationService"/>）。</param>
    /// <param name="settingsRepository">ルール一覧・<c>ApplyAllMatchingRules</c>設定の取得元。</param>
    /// <param name="ocrService">Phase2向けOCR抽出の呼び出し口。現時点では未使用（省略可）。</param>
    /// <param name="pythonApiClient">Phase2向けAI/SLM解析の呼び出し口。現時点では未使用（省略可）。</param>
    /// <param name="defaultConflictPolicy">
    /// Move/Copyアクション実行時の同名衝突ポリシー（<c>RuleAction</c>自体は保持しないため、
    /// <c>AppSettings</c>相当のサービス既定値として渡す）。既定は<see cref="ConflictPolicy.AutoRename"/>。
    /// </param>
    public ProcessingCoordinator(
        IRuleEngine ruleEngine,
        IHistoryRepository historyRepository,
        IFileOperationService fileOperationService,
        ISettingsRepository settingsRepository,
        IOcrService? ocrService = null,
        IPythonApiClient? pythonApiClient = null,
        ConflictPolicy defaultConflictPolicy = ConflictPolicy.AutoRename)
    {
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
        _fileOperationService = fileOperationService ?? throw new ArgumentNullException(nameof(fileOperationService));
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _ocrService = ocrService;
        _pythonApiClient = pythonApiClient;
        _defaultConflictPolicy = defaultConflictPolicy;
    }

    /// <summary>
    /// <see cref="FileStabilityDetector.FileStabilized"/>イベントへ本コーディネーターを接続する
    /// （1-5との実配線）。
    /// </summary>
    public void AttachTo(FileStabilityDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        detector.FileStabilized += OnFileStabilized;
    }

    public void Detach(FileStabilityDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        detector.FileStabilized -= OnFileStabilized;
    }

    private async void OnFileStabilized(object? sender, FileStableEventArgs e)
    {
        try
        {
            await ProcessAsync(e.Metadata).ConfigureAwait(false);
        }
        catch
        {
            // イベントハンドラから例外を漏らさない
            // （FileStabilityDetectorの単一集約ワーカーループを止めないため）。
        }
    }

    /// <summary>
    /// 安定確認済みの1ファイルに対し、ルール評価からファイル操作・履歴更新までの
    /// 一連のパイプラインを実行する。<see cref="FileStabilityDetector"/>からのイベント経由に限らず、
    /// 直接呼び出すこともできる（テスト・手動実行・定期走査からの再投入にも利用可能）。
    /// </summary>
    /// <returns>実行した各ファイル操作の最終状態の<see cref="HistoryRecord"/>一覧（一致ルールが無ければ空）。</returns>
    public async Task<IReadOnlyList<HistoryRecord>> ProcessAsync(FileMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!File.Exists(metadata.FullPath))
        {
            // 安定検知後、処理開始までの間にユーザー操作等で消失した場合は何もしない。
            var empty = Array.Empty<HistoryRecord>();
            ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, empty));
            return empty;
        }

        // Phase2拡張ポイント（OCR/AI解析）: 呼び出し口のみ用意し、現時点では実際には呼び出さない。
        await EnrichWithAiMetadataIfNeededAsync(metadata, ct).ConfigureAwait(false);

        AppSettings settings = await _settingsRepository.LoadSettingsAsync(ct).ConfigureAwait(false);
        List<RuleModel> rules = await _settingsRepository.LoadRulesAsync(ct).ConfigureAwait(false);

        RuleEvaluationResult evaluation = _ruleEngine.Evaluate(metadata, rules, settings.ApplyAllMatchingRules);
        if (!evaluation.IsMatched)
        {
            var empty = Array.Empty<HistoryRecord>();
            ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, empty));
            return empty;
        }

        IReadOnlyList<RuleModel> rulesToApply = settings.ApplyAllMatchingRules
            ? evaluation.AllMatchedRules
            : new List<RuleModel> { evaluation.MatchedRule! };

        var records = new List<HistoryRecord>();
        string currentPath = metadata.FullPath;

        foreach (var rule in rulesToApply)
        {
            foreach (var action in rule.Actions)
            {
                ct.ThrowIfCancellationRequested();

                ActionOutcome outcome = await ExecuteActionAsync(currentPath, action, ct).ConfigureAwait(false);
                if (outcome.Record != null)
                {
                    records.Add(outcome.Record);
                }
                currentPath = outcome.NextPath;

                if (outcome.StopChain)
                {
                    ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, records));
                    return records;
                }
            }
        }

        ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, records));
        return records;
    }

    // --- 1アクション分の Planned→Executing→Completed/Failed 実行 ---------------------------

    private readonly record struct ActionOutcome(HistoryRecord? Record, string NextPath, bool StopChain);

    private async Task<ActionOutcome> ExecuteActionAsync(string currentPath, RuleAction action, CancellationToken ct)
    {
        if (!TryMapOperationType(action.Type, out OperationType opType))
        {
            // 未知のaction typeは安全側で無視し、後続へ継続する。
            return new ActionOutcome(null, currentPath, StopChain: false);
        }

        if (!File.Exists(currentPath))
        {
            // 前段のアクション（Recycle等）で既に対象が消失している → これ以上進められない。
            return new ActionOutcome(null, currentPath, StopChain: true);
        }

        var fileInfo = new FileInfo(currentPath);
        string lightweightHash = HashHelper.ComputeLightweightHash(currentPath);

        var record = new HistoryRecord
        {
            OpType = opType,
            SourcePath = currentPath,
            DestinationPath = BuildIntendedDestinationPath(currentPath, action, opType),
            FileSizeBytes = fileInfo.Length,
            FileLastModifiedUtc = fileInfo.LastWriteTimeUtc,
            LightweightHash = lightweightHash,
            State = OperationState.Planned,
        };

        // 1-3: 操作実行前にPlannedとしてDBへ事前記録。
        long id = await _historyRepository.InsertAsync(record, ct).ConfigureAwait(false);
        record.Id = id;

        await _historyRepository.UpdateStateAsync(id, OperationState.Executing, ct: ct).ConfigureAwait(false);
        record.State = OperationState.Executing;

        OperationResult opResult;
        try
        {
            // 1-8: 実ファイル操作。
            opResult = await ExecuteFileOperationAsync(currentPath, action, opType, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _historyRepository.UpdateStateAsync(id, OperationState.Failed, ex.Message, ct).ConfigureAwait(false);
            record.State = OperationState.Failed;
            record.ErrorMessage = ex.Message;
            return new ActionOutcome(record, currentPath, StopChain: true);
        }

        if (opResult.Success && !opResult.WasSkippedDueToConflict)
        {
            await _historyRepository.UpdateStateAsync(id, OperationState.Completed, ct: ct).ConfigureAwait(false);
            record.State = OperationState.Completed;
            record.DestinationPath = opResult.FinalPath ?? record.DestinationPath;

            // Move/Renameは対象ファイルの所在自体が変わるため、以降はこの新しいパスを対象とする。
            // Copyは元ファイルを維持するため対象パスは変えない。
            string nextPath = opType is OperationType.Move or OperationType.Rename
                ? (opResult.FinalPath ?? currentPath)
                : currentPath;

            return new ActionOutcome(record, nextPath, StopChain: false);
        }

        if (opResult.Success && opResult.WasSkippedDueToConflict)
        {
            // ConflictPolicy.Skipによる意図的な無処理。失敗ではないため後続へ継続する
            // （対象ファイルは元のパスのまま）。
            const string skipMessage = "同名衝突のためスキップされました（Skipポリシー）。";
            await _historyRepository.UpdateStateAsync(id, OperationState.Completed, skipMessage, ct).ConfigureAwait(false);
            record.State = OperationState.Completed;
            record.ErrorMessage = skipMessage;
            record.DestinationPath = null;
            return new ActionOutcome(record, currentPath, StopChain: false);
        }

        // 失敗（ConflictPolicy.PromptUserによる要確認を含む）。ファイルの所在が不確実になるため中断する。
        string errorMessage = opResult.ErrorMessage ?? "不明なエラーにより操作に失敗しました。";
        await _historyRepository.UpdateStateAsync(id, OperationState.Failed, errorMessage, ct).ConfigureAwait(false);
        record.State = OperationState.Failed;
        record.ErrorMessage = errorMessage;
        return new ActionOutcome(record, currentPath, StopChain: true);
    }

    private Task<OperationResult> ExecuteFileOperationAsync(string currentPath, RuleAction action, OperationType opType, CancellationToken ct)
    {
        return opType switch
        {
            OperationType.Move => _fileOperationService.MoveAsync(currentPath, RequireDestination(action), _defaultConflictPolicy, ct),
            OperationType.Copy => _fileOperationService.CopyAsync(currentPath, RequireDestination(action), _defaultConflictPolicy, ct),
            // Phase1時点ではPattern（テンプレート文字列）をそのまま新ファイル名として使用する。
            // 日付・会社名等のプレースホルダー展開はPhase2（OCR/AI連携）で対応する。
            OperationType.Rename => _fileOperationService.RenameAsync(currentPath, RequirePattern(action), ct),
            OperationType.Recycle => _fileOperationService.RecycleAsync(currentPath, ct),
            _ => throw new InvalidOperationException($"未対応のOperationTypeです: {opType}"),
        };
    }

    private static string RequireDestination(RuleAction action)
        => !string.IsNullOrWhiteSpace(action.Destination)
            ? action.Destination
            : throw new InvalidOperationException($"アクション種別'{action.Type}'にはdestinationの指定が必須です。");

    private static string RequirePattern(RuleAction action)
        => !string.IsNullOrWhiteSpace(action.Pattern)
            ? action.Pattern
            : throw new InvalidOperationException("rename アクションにはpatternの指定が必須です。");

    private static string? BuildIntendedDestinationPath(string currentPath, RuleAction action, OperationType opType) => opType switch
    {
        OperationType.Move or OperationType.Copy => !string.IsNullOrWhiteSpace(action.Destination)
            ? Path.Combine(action.Destination, Path.GetFileName(currentPath))
            : null,
        OperationType.Rename => !string.IsNullOrWhiteSpace(action.Pattern)
            ? Path.Combine(Path.GetDirectoryName(currentPath) ?? string.Empty, action.Pattern)
            : null,
        _ => null, // Recycleに移動先の概念はない
    };

    private static bool TryMapOperationType(string actionType, out OperationType opType)
    {
        switch (actionType?.Trim().ToLowerInvariant())
        {
            case "move": opType = OperationType.Move; return true;
            case "copy": opType = OperationType.Copy; return true;
            case "rename": opType = OperationType.Rename; return true;
            case "recycle": opType = OperationType.Recycle; return true;
            default: opType = default; return false;
        }
    }

    /// <summary>
    /// Phase2拡張ポイント: OCR抽出（<see cref="IOcrService"/>）とAI/SLM解析
    /// （<see cref="IPythonApiClient"/>）の呼び出し口。現時点では未使用（ダミー）で、
    /// <paramref name="metadata"/>のOcrText/AiCategoryを書き換えない。
    /// </summary>
    private Task EnrichWithAiMetadataIfNeededAsync(FileMetadata metadata, CancellationToken ct)
    {
        _ = _ocrService;      // Phase2で使用予定（現時点では未使用）
        _ = _pythonApiClient; // Phase2で使用予定（現時点では未使用）
        _ = metadata;
        _ = ct;
        return Task.CompletedTask;
    }
}
