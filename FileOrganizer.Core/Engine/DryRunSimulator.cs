using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Core.Utils;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>1ファイルに対して1個の<see cref="RuleAction"/>を適用した場合に「起きるはずのこと」の予測結果。</summary>
public sealed class DryRunActionPlan
{
    public OperationType OpType { get; init; }

    /// <summary>このアクション適用直前の（このファイルにとっての）現在パス。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// 実際に配置されるであろう最終パス（<see cref="ConflictResolver"/>による連番付与後）。
    /// <see cref="WillSkip"/>または<see cref="RequiresConfirmation"/>がtrue、あるいは
    /// <see cref="OperationType.Recycle"/>の場合はnull。
    /// </summary>
    public string? PlannedDestinationPath { get; init; }

    /// <summary><see cref="ConflictPolicy.Skip"/>により実行時に何も行われない見込み。</summary>
    public bool WillSkip { get; init; }

    /// <summary>
    /// 同名衝突（<see cref="ConflictPolicy.PromptUser"/>）や設定不備により、実行時に
    /// ユーザー確認が必要になる見込み（このファイルに対する以降のアクションは予測を打ち切る）。
    /// </summary>
    public bool RequiresConfirmation { get; init; }
}

/// <summary>1ファイルに対するDry Run結果。</summary>
public sealed class DryRunPlanEntry
{
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>いずれかのルールに一致したか。falseの場合、このファイルは何も処理されない。</summary>
    public bool IsMatched { get; init; }

    /// <summary>適用されるルール名（<c>ApplyAllMatchingRules=true</c>時は最優先ルールの名前）。</summary>
    public string? MatchedRuleName { get; init; }

    /// <summary>実行されるであろうアクションの予測結果（優先順位・実行順）。</summary>
    public IReadOnlyList<DryRunActionPlan> Actions { get; init; } = Array.Empty<DryRunActionPlan>();
}

/// <summary>
/// 仕様書§3.1「今すぐ整理（Dry Run）」機能を実装する。実際のファイル操作は一切行わず、
/// 1-7 <see cref="IRuleEngine"/>（<see cref="RuleEvaluator"/>）によるルール評価と、
/// 1-8 <see cref="Services.FileOperationService"/>が実操作時に使うのと同じ同名衝突解決ロジック
/// （<see cref="ConflictResolver"/>）を流用して、「どのファイルがどこに移動されるか」の
/// 差分リストのみを算出する。
/// </summary>
/// <remarks>
/// <see cref="ProcessingCoordinator"/>と同じ「1ルール内の複数アクションを順に適用し、
/// Move/Renameで対象パスを更新、Copy/衝突スキップは据え置き、Recycle/確認要求で打ち切り」という
/// アクション連鎖ロジックを、実I/Oを伴わない形で再現する。
/// </remarks>
public sealed class DryRunSimulator
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ConflictPolicy _defaultConflictPolicy;
    private readonly IOcrService? _ocrService;
    private readonly IPythonApiClient? _pythonApiClient;

    /// <param name="ruleEngine">1-7 ルール評価エンジン（<see cref="RuleEvaluator"/>）。</param>
    /// <param name="defaultConflictPolicy">
    /// Move/Copyの同名衝突解決に使う既定ポリシー。実運用（<see cref="ProcessingCoordinator"/>）と
    /// 同じ値を渡すことで、実行結果と一致するシミュレーションになる。既定は<see cref="ConflictPolicy.AutoRename"/>。
    /// </param>
    public DryRunSimulator(
        IRuleEngine ruleEngine,
        ConflictPolicy defaultConflictPolicy = ConflictPolicy.AutoRename,
        IOcrService? ocrService = null,
        IPythonApiClient? pythonApiClient = null)
    {
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _defaultConflictPolicy = defaultConflictPolicy;
        _ocrService = ocrService;
        _pythonApiClient = pythonApiClient;
    }

    /// <summary>
    /// 指定フォルダ配下のファイルを列挙し（<see cref="Watcher.PeriodicScanner"/>と同様、
    /// 隠し/システムファイル・シンボリックリンク・<c>.lnk</c>は除外）、<see cref="Simulate"/>を行う。
    /// 「今すぐ整理（Dry Run）」のUIから、既存ファイル一括分の差分プレビューを得る想定のエントリポイント。
    /// </summary>
    public async Task<IReadOnlyList<DryRunPlanEntry>> SimulateFolderAsync(
        string folderPath,
        bool includeSubdirectories,
        IReadOnlyList<RuleModel> rules,
        bool applyAllMatchingRules,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(rules);

        if (!Directory.Exists(folderPath))
        {
            return Array.Empty<DryRunPlanEntry>();
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubdirectories,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        var metadataList = new List<FileMetadata>();
        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", enumerationOptions))
        {
            ct.ThrowIfCancellationRequested();

            if (string.Equals(Path.GetExtension(filePath), ".lnk", StringComparison.OrdinalIgnoreCase))
                continue;

            var info = new FileInfo(filePath);
            metadataList.Add(new FileMetadata
            {
                FullPath = filePath,
                FileName = info.Name,
                Extension = info.Extension,
                SizeBytes = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                CreatedTimeUtc = info.CreationTimeUtc,
            });
        }

        return await SimulateFilesAsync(metadataList, rules, applyAllMatchingRules, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drop Zone等で指定されたファイルだけをプレビューする。AI条件がある場合は実処理と同じく
    /// C# OCRで本文を得てPythonへ本文だけを渡す（Pythonは<c>FilePath</c>を開かない）。
    /// </summary>
    public async Task<IReadOnlyList<DryRunPlanEntry>> SimulateFilesAsync(
        IEnumerable<FileMetadata> files,
        IReadOnlyList<RuleModel> rules,
        bool applyAllMatchingRules,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(rules);

        var entries = new List<DryRunPlanEntry>();
        bool needsAnalysis = RequiresAiEnrichment(rules);
        foreach (FileMetadata metadata in files)
        {
            ct.ThrowIfCancellationRequested();
            AnalyzeResponse? analysis = needsAnalysis
                ? await TryEnrichAsync(metadata, ct).ConfigureAwait(false)
                : null;
            entries.Add(SimulateOne(metadata, rules, applyAllMatchingRules, analysis));
        }

        return entries;
    }

    /// <summary>
    /// 既に列挙済みの<see cref="FileMetadata"/>一覧に対してルール評価と衝突解決予測のみを行い、
    /// 実ファイル操作を一切行わずに差分リストを算出する。
    /// </summary>
    public IReadOnlyList<DryRunPlanEntry> Simulate(
        IEnumerable<FileMetadata> files,
        IReadOnlyList<RuleModel> rules,
        bool applyAllMatchingRules)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(rules);

        var entries = new List<DryRunPlanEntry>();

        foreach (var metadata in files)
        {
            entries.Add(SimulateOne(metadata, rules, applyAllMatchingRules, analysis: null));
        }

        return entries;
    }

    private DryRunPlanEntry SimulateOne(
        FileMetadata metadata,
        IReadOnlyList<RuleModel> rules,
        bool applyAllMatchingRules,
        AnalyzeResponse? analysis)
    {
        RuleEvaluationResult evaluation = _ruleEngine.Evaluate(metadata, rules, applyAllMatchingRules);
        if (!evaluation.IsMatched)
        {
            return new DryRunPlanEntry { SourcePath = metadata.FullPath, IsMatched = false };
        }

        IReadOnlyList<RuleModel> rulesToApply = applyAllMatchingRules
            ? evaluation.AllMatchedRules
            : new List<RuleModel> { evaluation.MatchedRule! };

        return new DryRunPlanEntry
        {
            SourcePath = metadata.FullPath,
            IsMatched = true,
            MatchedRuleName = rulesToApply.Count > 0 ? rulesToApply[0].Name : null,
            Actions = SimulateActionChain(metadata.FullPath, rulesToApply, analysis),
        };
    }

    private List<DryRunActionPlan> SimulateActionChain(
        string startPath,
        IReadOnlyList<RuleModel> rulesToApply,
        AnalyzeResponse? analysis)
    {
        var results = new List<DryRunActionPlan>();
        string currentPath = startPath;

        foreach (var rule in rulesToApply)
        {
            foreach (var action in rule.Actions)
            {
                var plan = SimulateAction(currentPath, action, analysis);
                if (plan is null) continue; // 未知のaction typeは無視

                results.Add(plan);

                if (plan.RequiresConfirmation)
                {
                    // 実行時の分岐が確定できないため、このファイルに対する以降の予測は打ち切る。
                    return results;
                }

                if (plan.WillSkip)
                {
                    // ProcessingCoordinator同様、意図的スキップは元パスのまま後続へ進む。
                    continue;
                }

                if (plan.OpType == OperationType.Recycle)
                {
                    // 対象ファイル自体が消滅するため、以降のアクションを続ける意味がない。
                    return results;
                }

                if (plan.OpType is OperationType.Move or OperationType.Rename && plan.PlannedDestinationPath != null)
                {
                    currentPath = plan.PlannedDestinationPath;
                }
                // Copyは元ファイルを維持するため対象パスは変えない。
            }
        }

        return results;
    }

    private DryRunActionPlan? SimulateAction(string currentPath, RuleAction action, AnalyzeResponse? analysis)
    {
        if (!TryMapOperationType(action.Type, out OperationType opType))
        {
            return null;
        }

        return opType switch
        {
            OperationType.Move or OperationType.Copy => SimulateMoveOrCopy(currentPath, action, opType),
            OperationType.Rename => SimulateRename(currentPath, action, analysis),
            OperationType.Recycle => new DryRunActionPlan { OpType = OperationType.Recycle, SourcePath = currentPath },
            _ => null,
        };
    }

    private DryRunActionPlan SimulateMoveOrCopy(string currentPath, RuleAction action, OperationType opType)
    {
        if (string.IsNullOrWhiteSpace(action.Destination))
        {
            return new DryRunActionPlan { OpType = opType, SourcePath = currentPath, RequiresConfirmation = true };
        }

        string desiredFileName = Path.GetFileName(currentPath);
        var resolution = ConflictResolver.Resolve(action.Destination, desiredFileName, _defaultConflictPolicy);

        return resolution.Outcome switch
        {
            ConflictResolutionOutcome.NoConflict or ConflictResolutionOutcome.Resolved => new DryRunActionPlan
            {
                OpType = opType,
                SourcePath = currentPath,
                PlannedDestinationPath = Path.Combine(action.Destination, resolution.ResolvedFileName!),
            },
            ConflictResolutionOutcome.Skip => new DryRunActionPlan
            {
                OpType = opType,
                SourcePath = currentPath,
                WillSkip = true,
            },
            _ => new DryRunActionPlan
            {
                OpType = opType,
                SourcePath = currentPath,
                RequiresConfirmation = true,
            },
        };
    }

    private DryRunActionPlan SimulateRename(string currentPath, RuleAction action, AnalyzeResponse? analysis)
    {
        if (string.IsNullOrWhiteSpace(action.Pattern))
        {
            return new DryRunActionPlan { OpType = OperationType.Rename, SourcePath = currentPath, RequiresConfirmation = true };
        }

        string expanded = RenamePatternExpander.Expand(action.Pattern, currentPath, analysis);
        string sanitized = PathSanitizer.SanitizeFileName(expanded);

        string? directory = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new DryRunActionPlan { OpType = OperationType.Rename, SourcePath = currentPath, RequiresConfirmation = true };
        }

        string candidatePath = Path.Combine(directory, sanitized);
        bool isSelf = string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase);
        if (isSelf)
        {
            return new DryRunActionPlan
            {
                OpType = OperationType.Rename,
                SourcePath = currentPath,
                PlannedDestinationPath = candidatePath,
            };
        }

        ConflictResolution resolution = ConflictResolver.Resolve(directory, sanitized, _defaultConflictPolicy);
        return resolution.Outcome switch
        {
            ConflictResolutionOutcome.NoConflict or ConflictResolutionOutcome.Resolved => new DryRunActionPlan
            {
                OpType = OperationType.Rename,
                SourcePath = currentPath,
                PlannedDestinationPath = Path.Combine(directory, resolution.ResolvedFileName!),
            },
            ConflictResolutionOutcome.Skip => new DryRunActionPlan
            {
                OpType = OperationType.Rename,
                SourcePath = currentPath,
                WillSkip = true,
            },
            _ => new DryRunActionPlan
            {
                OpType = OperationType.Rename,
                SourcePath = currentPath,
                RequiresConfirmation = true,
            },
        };
    }

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

    private static bool RequiresAiEnrichment(IReadOnlyList<RuleModel> rules)
        => rules.Any(rule => rule.Enabled &&
            (rule.Conditions.Any(condition => condition.Type is "ocr_contains" or "ai_category") ||
             rule.Actions.Any(action => action.Type.Equals("rename", StringComparison.OrdinalIgnoreCase) &&
                 (action.Pattern?.Contains("{category}", StringComparison.OrdinalIgnoreCase) == true ||
                  action.Pattern?.Contains("{date}", StringComparison.OrdinalIgnoreCase) == true ||
                  action.Pattern?.Contains("{company}", StringComparison.OrdinalIgnoreCase) == true ||
                  action.Pattern?.Contains("{document_type}", StringComparison.OrdinalIgnoreCase) == true))));

    private async Task<AnalyzeResponse?> TryEnrichAsync(FileMetadata metadata, CancellationToken ct)
    {
        if (_ocrService is null)
        {
            return null;
        }

        try
        {
            if (!await _ocrService.IsLanguagePackAvailableAsync().ConfigureAwait(false))
            {
                return null;
            }

            string? text = await _ocrService.ExtractTextAsync(metadata.FullPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            metadata.OcrText = text;
            if (_pythonApiClient is null)
            {
                return null;
            }
            AnalyzeResponse? response = await _pythonApiClient.AnalyzeAsync(new AnalyzeRequest
            {
                // Pythonでは表示・拡張子推定用メタデータ。通常IPCでこのパスを開いてはならない。
                FilePath = metadata.FullPath,
                OcrText = text.Length <= AnalyzeRequest.MaxOcrTextLength
                    ? text
                    : text[..AnalyzeRequest.MaxOcrTextLength],
                ExtractFields = ["date", "company", "document_type", "category"],
            }, ct).ConfigureAwait(false);

            if (response?.Success == true)
            {
                metadata.AiCategory = response.Category;
                return response;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // OCR/AI失敗時はファイル名等の基本ルールへgracefulに退避する。
        }

        return null;
    }
}
