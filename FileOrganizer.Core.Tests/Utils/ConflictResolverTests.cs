using FileOrganizer.Core.Utils;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Utils;

/// <summary>
/// 仕様書§6「同名衝突の防止」の解決ロジックを検証する。
/// 対象: <see cref="ConflictResolver"/>（<c>FileOperationService</c>・<c>DryRunSimulator</c>共通利用）。
/// </summary>
public class ConflictResolverTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "ConflictResolver", Guid.NewGuid().ToString("N"));

    public ConflictResolverTests()
    {
        Directory.CreateDirectory(_workDir);
    }

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
    public void Resolve_衝突がなければNoConflictでそのままの名前を返す()
    {
        var result = ConflictResolver.Resolve(_workDir, "a.txt", ConflictPolicy.AutoRename);

        Assert.Equal(ConflictResolutionOutcome.NoConflict, result.Outcome);
        Assert.Equal("a.txt", result.ResolvedFileName);
    }

    [Fact]
    public void Resolve_AutoRenameで衝突があれば連番を付与する()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "x");

        var result = ConflictResolver.Resolve(_workDir, "a.txt", ConflictPolicy.AutoRename);

        Assert.Equal(ConflictResolutionOutcome.Resolved, result.Outcome);
        Assert.Equal("a_1.txt", result.ResolvedFileName);
    }

    [Fact]
    public void Resolve_AutoRenameで連番も使用済みなら次の番号にする()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_workDir, "a_1.txt"), "x");
        File.WriteAllText(Path.Combine(_workDir, "a_2.txt"), "x");

        var result = ConflictResolver.Resolve(_workDir, "a.txt", ConflictPolicy.AutoRename);

        Assert.Equal("a_3.txt", result.ResolvedFileName);
    }

    [Fact]
    public void Resolve_Skipポリシーなら衝突時にSkipを返す()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "x");

        var result = ConflictResolver.Resolve(_workDir, "a.txt", ConflictPolicy.Skip);

        Assert.Equal(ConflictResolutionOutcome.Skip, result.Outcome);
        Assert.Null(result.ResolvedFileName);
    }

    [Fact]
    public void Resolve_PromptUserポリシーなら衝突時にPromptRequiredを返す()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "x");

        var result = ConflictResolver.Resolve(_workDir, "a.txt", ConflictPolicy.PromptUser);

        Assert.Equal(ConflictResolutionOutcome.PromptRequired, result.Outcome);
        Assert.Null(result.ResolvedFileName);
    }

    [Fact]
    public void Resolve_フォルダとの衝突も検知する()
    {
        Directory.CreateDirectory(Path.Combine(_workDir, "a.txt")); // 同名のフォルダ

        var result = ConflictResolver.Resolve(_workDir, "a.txt", ConflictPolicy.AutoRename);

        Assert.Equal(ConflictResolutionOutcome.Resolved, result.Outcome);
        Assert.Equal("a_1.txt", result.ResolvedFileName);
    }

    [Fact]
    public void GenerateNonConflictingFileName_拡張子を保持したまま連番を付与する()
    {
        File.WriteAllText(Path.Combine(_workDir, "report.pdf"), "x");

        string name = ConflictResolver.GenerateNonConflictingFileName(_workDir, "report.pdf");

        Assert.Equal("report_1.pdf", name);
    }
}
