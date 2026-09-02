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
/// Move/Renameで対象パスを更新、Copyは据え置き、Recycle/確認要求/スキップで打ち切り」という
/// アクション連鎖ロジックを、実I/Oを伴わない形で再現する。
/// </remarks>
public sealed class DryRunSimulator
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ConflictPolicy _defaultConflictPolicy;

    /// <param name="ruleEngine">1-7 ルール評価エンジン（<see cref="RuleEvaluator"/>）。</param>
    /// <param name="defaultConflictPolicy">
    /// Move/Copyの同名衝突解決に使う既定ポリシー。実運用（<see cref="ProcessingCoordinator"/>）と
    /// 同じ値を渡すことで、実行結果と一致するシミュレーションになる。既定は<see cref="ConflictPolicy.AutoRename"/>。
    /// </param>
    public DryRunSimulator(IRuleEngine ruleEngine, ConflictPolicy defaultConflictPolicy = ConflictPolicy.AutoRename)
    {
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _defaultConflictPolicy = defaultConflictPolicy;
    }

    /// <summary>
    /// 指定フォルダ配下のファイルを列挙し（<see cref="Watcher.PeriodicScanner"/>と同様、
    /// 隠し/システムファイル・シンボリックリンク・<c>.lnk</c>は除外）、<see cref="Simulate"/>を行う。
    /// 「今すぐ整理（Dry Run）」のUIから、既存ファイル一括分の差分プレビューを得る想定のエントリポイント。
    /// </summary>
    public Task<IReadOnlyList<DryRunPlanEntry>> SimulateFolderAsync(
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
            return Task.FromResult<IReadOnlyList<DryRunPlanEntry>>(Array.Empty<DryRunPlanEntry>());
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

        return Task.FromResult(Simulate(metadataList, rules, applyAllMatchingRules));
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
            var evaluation = _ruleEngine.Evaluate(metadata, rules, applyAllMatchingRules);
            if (!evaluation.IsMatched)
            {
                entries.Add(new DryRunPlanEntry { SourcePath = metadata.FullPath, IsMatched = false });
                continue;
            }

            IReadOnlyList<RuleModel> rulesToApply = applyAllMatchingRules
                ? evaluation.AllMatchedRules
                : new List<RuleModel> { evaluation.MatchedRule! };

            var actions = SimulateActionChain(metadata.FullPath, rulesToApply);

            entries.Add(new DryRunPlanEntry
            {
                SourcePath = metadata.FullPath,
                IsMatched = true,
                MatchedRuleName = rulesToApply.Count > 0 ? rulesToApply[0].Name : null,
                Actions = actions,
            });
        }

        return entries;
    }

    private List<DryRunActionPlan> SimulateActionChain(string startPath, IReadOnlyList<RuleModel> rulesToApply)
    {
        var results = new List<DryRunActionPlan>();
        string currentPath = startPath;

        foreach (var rule in rulesToApply)
        {
            foreach (var action in rule.Actions)
            {
                var plan = SimulateAction(currentPath, action);
                if (plan is null) continue; // 未知のaction typeは無視

                results.Add(plan);

                if (plan.WillSkip || plan.RequiresConfirmation)
                {
                    // 実行時の分岐が確定できないため、このファイルに対する以降の予測は打ち切る。
                    return results;
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

    private DryRunActionPlan? SimulateAction(string currentPath, RuleAction action)
    {
        if (!TryMapOperationType(action.Type, out OperationType opType))
        {
            return null;
        }

        return opType switch
        {
            OperationType.Move or OperationType.Copy => SimulateMoveOrCopy(currentPath, action, opType),
            OperationType.Rename => SimulateRename(currentPath, action),
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

    private static DryRunActionPlan SimulateRename(string currentPath, RuleAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Pattern))
        {
            return new DryRunActionPlan { OpType = OperationType.Rename, SourcePath = currentPath, RequiresConfirmation = true };
        }

        // Phase1時点ではPatternをそのまま新ファイル名として使用する
        // （プレースホルダー展開はPhase2、ProcessingCoordinatorと同一の扱い）。
        string sanitized = PathSanitizer.SanitizeFileName(action.Pattern);

        string? directory = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new DryRunActionPlan { OpType = OperationType.Rename, SourcePath = currentPath, RequiresConfirmation = true };
        }

        string candidatePath = Path.Combine(directory, sanitized);

        // Undo同様、リネームは自動別名復元を行わない仕様（§3.2）に合わせ、
        // 衝突時は常に要確認とする（大文字小文字のみの変更は自分自身なので衝突として扱わない）。
        bool isSelf = string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase);
        bool conflict = !isSelf && (File.Exists(candidatePath) || Directory.Exists(candidatePath));

        return conflict
            ? new DryRunActionPlan { OpType = OperationType.Rename, SourcePath = currentPath, RequiresConfirmation = true }
            : new DryRunActionPlan { OpType = OperationType.Rename, SourcePath = currentPath, PlannedDestinationPath = candidatePath };
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
}
