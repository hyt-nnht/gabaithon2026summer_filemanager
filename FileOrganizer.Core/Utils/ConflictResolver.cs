using System;
using System.IO;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Utils;

/// <summary>結果種別。</summary>
public enum ConflictResolutionOutcome
{
    /// <summary>移動先に同名の衝突がない。</summary>
    NoConflict,

    /// <summary>衝突があり、連番付与により空いている名前が見つかった。</summary>
    Resolved,

    /// <summary>衝突があり、<see cref="ConflictPolicy.Skip"/>により何もしない。</summary>
    Skip,

    /// <summary>衝突があり、<see cref="ConflictPolicy.PromptUser"/>によりユーザー確認が必要。</summary>
    PromptRequired,
}

public readonly record struct ConflictResolution(ConflictResolutionOutcome Outcome, string? ResolvedFileName);

/// <summary>
/// 仕様書§6「同名衝突の防止」の解決ロジック。移動・コピー先に同名ファイル/フォルダが存在する場合の
/// 判定（連番付与／スキップ／要確認）を1箇所にまとめ、実際にファイル操作を行う
/// <see cref="Services.FileOperationService"/>と、実操作を行わずシミュレーションのみ行う
/// <see cref="Engine.DryRunSimulator"/>の両方から共通利用する。
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// <paramref name="destinationDirectory"/>に<paramref name="desiredFileName"/>という名前で
    /// 置けるかどうかを判定し、<paramref name="policy"/>に従って解決する。
    /// </summary>
    public static ConflictResolution Resolve(string destinationDirectory, string desiredFileName, ConflictPolicy policy)
    {
        string candidatePath = Path.Combine(destinationDirectory, desiredFileName);
        bool conflicts = File.Exists(candidatePath) || Directory.Exists(candidatePath);
        if (!conflicts)
        {
            return new ConflictResolution(ConflictResolutionOutcome.NoConflict, desiredFileName);
        }

        return policy switch
        {
            // 上書き禁止が既定 → 連番付与で回避。
            ConflictPolicy.AutoRename => new ConflictResolution(
                ConflictResolutionOutcome.Resolved, GenerateNonConflictingFileName(destinationDirectory, desiredFileName)),
            ConflictPolicy.Skip => new ConflictResolution(ConflictResolutionOutcome.Skip, null),
            ConflictPolicy.PromptUser => new ConflictResolution(ConflictResolutionOutcome.PromptRequired, null),
            _ => new ConflictResolution(ConflictResolutionOutcome.Skip, null),
        };
    }

    /// <summary>
    /// <paramref name="destinationDirectory"/>内で<paramref name="desiredFileName"/>と衝突しない
    /// 連番付き名前（<c>_1</c>, <c>_2</c>, ...）を探索して返す。
    /// </summary>
    public static string GenerateNonConflictingFileName(string destinationDirectory, string desiredFileName)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(desiredFileName);
        string ext = Path.GetExtension(desiredFileName);

        for (int suffix = 1; suffix < int.MaxValue; suffix++)
        {
            string candidateName = $"{nameWithoutExt}_{suffix}{ext}";
            string candidatePath = Path.Combine(destinationDirectory, candidateName);
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidateName;
            }
        }

        // 通常到達しない（int.MaxValue回衝突することは実運用上あり得ない）。
        throw new InvalidOperationException($"連番付与による空き名称の探索に失敗しました: {desiredFileName}");
    }
}
