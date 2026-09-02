using System.Linq;
using FileOrganizer.Core.Engine;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Engine;

/// <summary>
/// 仕様書§3.1「今すぐ整理（Dry Run）」機能の受け入れ基準を検証する。
/// 対象: <see cref="DryRunSimulator"/>（1-7 <see cref="RuleEvaluator"/>実実装を使用）。
/// いずれのテストも実ファイル操作が一切発生しないことを合わせて確認する。
/// </summary>
public class DryRunSimulatorTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "DryRunSimulator", Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _destDir;

    public DryRunSimulatorTests()
    {
        _sourceDir = Path.Combine(_workDir, "source");
        _destDir = Path.Combine(_workDir, "dest");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);
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

    private string CreateSourceFile(string fileName, string content = "content")
    {
        string path = Path.Combine(_sourceDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileMetadata BuildMetadata(string path)
    {
        var info = new FileInfo(path);
        return new FileMetadata
        {
            FullPath = path,
            FileName = info.Name,
            Extension = info.Extension,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            CreatedTimeUtc = info.CreationTimeUtc,
        };
    }

    private static RuleCondition Cond(string type, string op, object? value) => new() { Type = type, Operator = op, Value = value };
    private static RuleAction MoveTo(string destination) => new() { Type = "move", Destination = destination };
    private static RuleAction RenameTo(string pattern) => new() { Type = "rename", Pattern = pattern };
    private static RuleAction Recycle() => new() { Type = "recycle" };

    private static RuleModel CreateRule(string name, RuleCondition condition, params RuleAction[] actions) => new()
    {
        Name = name,
        Enabled = true,
        Conditions = new List<RuleCondition> { condition },
        Actions = actions.ToList(),
    };

    private readonly DryRunSimulator _simulator = new(new RuleEvaluator());

    // --- 基本: 一致するルールの移動先を予測する（実ファイルは動かない） -------------------------

    [Fact]
    public void Simulate_一致するルールがあれば移動先を予測し実ファイルは動かさない()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel> { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };

        var result = _simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var entry = Assert.Single(result);
        Assert.True(entry.IsMatched);
        Assert.Equal("pdfをdestへ", entry.MatchedRuleName);
        var action = Assert.Single(entry.Actions);
        Assert.Equal(OperationType.Move, action.OpType);
        Assert.Equal(Path.Combine(_destDir, "report.pdf"), action.PlannedDestinationPath);
        Assert.False(action.WillSkip);
        Assert.False(action.RequiresConfirmation);

        // 実ファイルは一切動いていない。
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(_destDir, "report.pdf")));
    }

    [Fact]
    public void Simulate_一致するルールがなければIsMatchedはfalseになる()
    {
        string sourcePath = CreateSourceFile("report.docx");
        var rules = new List<RuleModel> { CreateRule("pdfのみ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };

        var result = _simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var entry = Assert.Single(result);
        Assert.False(entry.IsMatched);
        Assert.Empty(entry.Actions);
    }

    // --- 同名衝突予測（ConflictResolver流用） ------------------------------------------------

    [Fact]
    public void Simulate_同名衝突がある場合はAutoRenameで連番付きの予測になる()
    {
        File.WriteAllText(Path.Combine(_destDir, "report.pdf"), "existing");
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel> { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };
        var simulator = new DryRunSimulator(new RuleEvaluator(), ConflictPolicy.AutoRename);

        var result = simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var action = Assert.Single(Assert.Single(result).Actions);
        Assert.Equal(Path.Combine(_destDir, "report_1.pdf"), action.PlannedDestinationPath);
        // 予測のみで実際には何も作られていない。
        Assert.False(File.Exists(Path.Combine(_destDir, "report_1.pdf")));
    }

    [Fact]
    public void Simulate_Skipポリシーの場合は衝突時にWillSkipになる()
    {
        File.WriteAllText(Path.Combine(_destDir, "report.pdf"), "existing");
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel> { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };
        var simulator = new DryRunSimulator(new RuleEvaluator(), ConflictPolicy.Skip);

        var result = simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var action = Assert.Single(Assert.Single(result).Actions);
        Assert.True(action.WillSkip);
        Assert.Null(action.PlannedDestinationPath);
    }

    [Fact]
    public void Simulate_PromptUserポリシーの場合は衝突時にRequiresConfirmationになる()
    {
        File.WriteAllText(Path.Combine(_destDir, "report.pdf"), "existing");
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel> { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };
        var simulator = new DryRunSimulator(new RuleEvaluator(), ConflictPolicy.PromptUser);

        var result = simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var action = Assert.Single(Assert.Single(result).Actions);
        Assert.True(action.RequiresConfirmation);
    }

    // --- Renameの衝突予測（Undoと同様、自動別名復元は行わない） --------------------------------

    [Fact]
    public void Simulate_Renameで衝突がある場合はAutoRenameポリシーでも常に要確認になる()
    {
        CreateSourceFile("existing.pdf");
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel> { CreateRule("リネーム", Cond("extension", "equals", "pdf"), RenameTo("existing.pdf")) };
        var simulator = new DryRunSimulator(new RuleEvaluator(), ConflictPolicy.AutoRename);

        var result = simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var action = Assert.Single(Assert.Single(result).Actions);
        Assert.True(action.RequiresConfirmation);
        Assert.Null(action.PlannedDestinationPath);
    }

    // --- 複数アクションの連鎖予測（ApplyAllMatchingRules） -------------------------------------

    [Fact]
    public void Simulate_ApplyAllMatchingRulesで複数ルールのアクションが連鎖して予測される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel>
        {
            CreateRule("先にリネーム", Cond("extension", "equals", "pdf"), RenameTo("renamed.pdf")),
            CreateRule("次に移動", Cond("extension", "equals", "pdf"), MoveTo(_destDir)),
        };

        var result = _simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: true);

        var entry = Assert.Single(result);
        Assert.Equal(2, entry.Actions.Count);
        Assert.Equal(OperationType.Rename, entry.Actions[0].OpType);
        Assert.Equal(Path.Combine(_sourceDir, "renamed.pdf"), entry.Actions[0].PlannedDestinationPath);

        Assert.Equal(OperationType.Move, entry.Actions[1].OpType);
        Assert.Equal(Path.Combine(_sourceDir, "renamed.pdf"), entry.Actions[1].SourcePath); // 前段の結果を引き継ぐ
        Assert.Equal(Path.Combine(_destDir, "renamed.pdf"), entry.Actions[1].PlannedDestinationPath);
    }

    [Fact]
    public void Simulate_Recycleアクションの後は予測を打ち切る()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var rules = new List<RuleModel>
        {
            CreateRule("ゴミ箱してから移動（無意味だが打ち切り確認用）", Cond("extension", "equals", "pdf"), Recycle(), MoveTo(_destDir)),
        };

        var result = _simulator.Simulate(new[] { BuildMetadata(sourcePath) }, rules, applyAllMatchingRules: false);

        var actions = Assert.Single(result).Actions;
        Assert.Single(actions); // Move側は予測に含まれない
        Assert.Equal(OperationType.Recycle, actions[0].OpType);
    }

    // --- SimulateFolderAsync: フォルダ列挙（実ファイル操作なし） --------------------------------

    [Fact]
    public async Task SimulateFolderAsync_フォルダ内のファイルを列挙して予測する()
    {
        CreateSourceFile("a.pdf");
        CreateSourceFile("b.docx");
        var rules = new List<RuleModel> { CreateRule("pdfのみ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };

        var result = await _simulator.SimulateFolderAsync(_sourceDir, includeSubdirectories: false, rules, applyAllMatchingRules: false);

        Assert.Equal(2, result.Count);
        var pdfEntry = result.Single(e => e.SourcePath.EndsWith("a.pdf"));
        Assert.True(pdfEntry.IsMatched);
        var docxEntry = result.Single(e => e.SourcePath.EndsWith("b.docx"));
        Assert.False(docxEntry.IsMatched);

        // 実ファイルは一切動いていない。
        Assert.True(File.Exists(Path.Combine(_sourceDir, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(_sourceDir, "b.docx")));
    }

    [Fact]
    public async Task SimulateFolderAsync_隠しファイル_システムファイル_lnkは除外する()
    {
        string hiddenPath = CreateSourceFile("hidden.pdf");
        File.SetAttributes(hiddenPath, FileAttributes.Hidden);
        CreateSourceFile("shortcut.lnk");
        CreateSourceFile("normal.pdf");
        var rules = new List<RuleModel> { CreateRule("pdf", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) };

        var result = await _simulator.SimulateFolderAsync(_sourceDir, includeSubdirectories: false, rules, applyAllMatchingRules: false);

        Assert.Single(result);
        Assert.EndsWith("normal.pdf", result[0].SourcePath);
    }

    [Fact]
    public async Task SimulateFolderAsync_存在しないフォルダは空リストを返す()
    {
        var rules = new List<RuleModel>();
        var result = await _simulator.SimulateFolderAsync(
            Path.Combine(_workDir, "does-not-exist"), includeSubdirectories: false, rules, applyAllMatchingRules: false);

        Assert.Empty(result);
    }

    // --- 引数検証 -------------------------------------------------------------------------

    [Fact]
    public void Constructor_ruleEngineがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() => new DryRunSimulator(null!));
    }

    [Fact]
    public void Simulate_filesがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() => _simulator.Simulate(null!, new List<RuleModel>(), false));
    }
}
