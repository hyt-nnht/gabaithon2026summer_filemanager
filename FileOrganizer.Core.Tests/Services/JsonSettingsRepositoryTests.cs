using System.Linq;
using FileOrganizer.Core.Engine;
using FileOrganizer.Core.Services;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Services;

/// <summary>
/// 仕様書の「内部でRules.jsonを自動生成・プリセット同梱」、および
/// 「設定の非同期保存・プリセット復元」の受け入れ基準を検証する。
/// 対象: <see cref="JsonSettingsRepository"/>。実際のファイルI/Oを一時フォルダで行う。
/// </summary>
public class JsonSettingsRepositoryTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "JsonSettingsRepository", Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;
    private readonly string _rulesPath;

    public JsonSettingsRepositoryTests()
    {
        Directory.CreateDirectory(_workDir);
        _settingsPath = Path.Combine(_workDir, "settings.json");
        _rulesPath = Path.Combine(_workDir, "rules.json");
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

    private JsonSettingsRepository CreateRepository(int maxRetryAttempts = 3, TimeSpan? retryDelay = null)
        => new(_settingsPath, _rulesPath, maxRetryAttempts, retryDelay);

    // --- Settings: 読み込み・非同期保存 ------------------------------------------------------

    [Fact]
    public async Task LoadSettingsAsync_ファイル未存在時はAppSettingsの既定値を返す()
    {
        var repository = CreateRepository();

        var settings = await repository.LoadSettingsAsync();

        Assert.NotNull(settings);
        Assert.Equal(750, settings.StabilityCheckIntervalMs); // AppSettingsの既定値
        Assert.False(File.Exists(_settingsPath)); // Load側では自動生成しない（Rulesとは異なる）
    }

    [Fact]
    public async Task SaveSettingsAsync_保存後にLoadSettingsAsyncで同じ内容が読み込める()
    {
        var repository = CreateRepository();
        var original = new AppSettings
        {
            StabilityCheckIntervalMs = 500,
            PeriodicScanIntervalHours = 12,
            ApplyAllMatchingRules = true,
            PythonPort = 12345,
        };

        await repository.SaveSettingsAsync(original);
        Assert.True(File.Exists(_settingsPath));

        var loaded = await repository.LoadSettingsAsync();

        Assert.Equal(500, loaded.StabilityCheckIntervalMs);
        Assert.Equal(12, loaded.PeriodicScanIntervalHours);
        Assert.True(loaded.ApplyAllMatchingRules);
        Assert.Equal(12345, loaded.PythonPort);
    }

    [Fact]
    public async Task SaveSettingsAsync_保存されるJSONは人間が読める整形済み形式である()
    {
        var repository = CreateRepository();
        await repository.SaveSettingsAsync(new AppSettings());

        string json = await File.ReadAllTextAsync(_settingsPath);

        Assert.Contains('\n', json); // WriteIndented=trueであることの簡易確認
        Assert.Contains("stability_check_interval_ms", json); // JsonPropertyNameが適用されている
    }

    // --- Rules: 読み込み・プリセット自動生成 --------------------------------------------------

    [Fact]
    public async Task LoadRulesAsync_ファイル未存在時はプリセットを自動生成して保存する()
    {
        var repository = CreateRepository();
        Assert.False(File.Exists(_rulesPath));

        var rules = await repository.LoadRulesAsync();

        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.True(r.Enabled));
        Assert.All(rules, r => Assert.NotEmpty(r.Conditions));
        Assert.All(rules, r => Assert.NotEmpty(r.Actions));

        // 「内部でRules.jsonを自動生成」の確認: 実ファイルとして保存されている。
        Assert.True(File.Exists(_rulesPath));
        var reloaded = await repository.LoadRulesAsync();
        Assert.Equal(rules.Count, reloaded.Count);
    }

    [Fact]
    public async Task SaveRulesAsync_保存後にLoadRulesAsyncで同じ内容が読み込める()
    {
        var repository = CreateRepository();
        var rules = new List<RuleModel>
        {
            new()
            {
                Name = "カスタムルール",
                Enabled = true,
                WatchFolder = @"C:\watch",
                Conditions = new List<RuleCondition>
                {
                    new() { Type = "extension", Operator = "in", Value = new[] { "pdf", "docx" } },
                    new() { Type = "size_mb", Operator = "greater_than", Value = 5 },
                },
                Actions = new List<RuleAction>
                {
                    new() { Type = "move", Destination = @"D:\organized" },
                },
            },
        };

        await repository.SaveRulesAsync(rules);
        var loaded = await repository.LoadRulesAsync();

        var rule = Assert.Single(loaded);
        Assert.Equal("カスタムルール", rule.Name);
        Assert.Equal(@"C:\watch", rule.WatchFolder);
        Assert.Equal(2, rule.Conditions.Count);
        Assert.Equal("extension", rule.Conditions[0].Type);
        Assert.Equal("in", rule.Conditions[0].Operator);
        Assert.Single(rule.Actions);
        Assert.Equal(@"D:\organized", rule.Actions[0].Destination);
    }

    [Fact]
    public async Task SaveRulesAsync_カスタムルールを保存後もRuleEvaluatorで正しく評価できる()
    {
        // JSON往復後もRuleCondition.Value（JsonElement化される）がRuleEvaluatorで機能することを確認する。
        var repository = CreateRepository();
        await repository.SaveRulesAsync(new List<RuleModel>
        {
            new()
            {
                Name = "pdf",
                Enabled = true,
                Conditions = new List<RuleCondition> { new() { Type = "extension", Operator = "equals", Value = "pdf" } },
                Actions = new List<RuleAction> { new() { Type = "move", Destination = @"D:\organized" } },
            },
        });

        var loaded = await repository.LoadRulesAsync();
        var evaluator = new RuleEvaluator();
        var metadata = new FileMetadata { FullPath = @"C:\watch\a.pdf", FileName = "a.pdf", Extension = ".pdf" };

        var result = evaluator.Evaluate(metadata, loaded, applyAllMatchingRules: false);

        Assert.True(result.IsMatched);
    }

    // --- プリセット復元 ---------------------------------------------------------------------

    [Fact]
    public async Task RestorePresetRulesAsync_既存のカスタムルールをプリセットで上書きする()
    {
        var repository = CreateRepository();
        await repository.SaveRulesAsync(new List<RuleModel>
        {
            new() { Name = "ユーザーが作った独自ルール", Enabled = true },
        });

        await repository.RestorePresetRulesAsync();
        var rules = await repository.LoadRulesAsync();

        Assert.DoesNotContain(rules, r => r.Name == "ユーザーが作った独自ルール");
        Assert.NotEmpty(rules);
    }

    [Fact]
    public async Task RestorePresetRulesAsync_複数回呼んでも同じ内容のプリセットになる()
    {
        var repository = CreateRepository();

        await repository.RestorePresetRulesAsync();
        var first = await repository.LoadRulesAsync();

        await repository.RestorePresetRulesAsync();
        var second = await repository.LoadRulesAsync();

        Assert.Equal(first.Select(r => r.Name), second.Select(r => r.Name));
    }

    // --- バックアップ(.bak)と復旧 ------------------------------------------------------------

    [Fact]
    public async Task SaveSettingsAsync_2回目の保存で1回目の内容が_bakへ退避される()
    {
        var repository = CreateRepository();
        await repository.SaveSettingsAsync(new AppSettings { StabilityCheckIntervalMs = 111 });
        Assert.False(File.Exists(_settingsPath + ".bak")); // 1回目はまだ本ファイルが存在しないのでbakは作られない

        await repository.SaveSettingsAsync(new AppSettings { StabilityCheckIntervalMs = 222 });

        Assert.True(File.Exists(_settingsPath + ".bak"));
        string backupJson = await File.ReadAllTextAsync(_settingsPath + ".bak");
        Assert.Contains("111", backupJson); // 1回目（旧内容）がバックアップに残っている

        var current = await repository.LoadSettingsAsync();
        Assert.Equal(222, current.StabilityCheckIntervalMs); // 本ファイルは最新（2回目）の内容
    }

    [Fact]
    public async Task LoadSettingsAsync_本ファイルが破損していてもbakから復旧できる()
    {
        var repository = CreateRepository();
        await repository.SaveSettingsAsync(new AppSettings { StabilityCheckIntervalMs = 111 }); // bakはまだ無い
        await repository.SaveSettingsAsync(new AppSettings { StabilityCheckIntervalMs = 222 }); // bak = 111の内容

        // 本ファイルを破損させる（不正なJSON）。
        await File.WriteAllTextAsync(_settingsPath, "{ this is not valid json !!!");

        var recovered = await repository.LoadSettingsAsync();

        Assert.Equal(111, recovered.StabilityCheckIntervalMs); // bak（1つ前の正常な内容）から復旧
    }

    [Fact]
    public async Task LoadRulesAsync_本ファイルもbakも破損していればプリセットへフォールバックする()
    {
        var repository = CreateRepository();
        await repository.SaveRulesAsync(new List<RuleModel> { new() { Name = "r1" } });
        await repository.SaveRulesAsync(new List<RuleModel> { new() { Name = "r2" } }); // bak = r1の内容

        await File.WriteAllTextAsync(_rulesPath, "not valid json");
        await File.WriteAllTextAsync(_rulesPath + ".bak", "also not valid json");

        var rules = await repository.LoadRulesAsync();

        Assert.NotEmpty(rules);
        Assert.DoesNotContain(rules, r => r.Name is "r1" or "r2"); // プリセットに置き換わっている

        // 破損していたrules.json自体もプリセットで上書き保存されている（次回以降も正常に読める）。
        var reloaded = await repository.LoadRulesAsync();
        Assert.Equal(rules.Count, reloaded.Count);
    }

    // --- 保存失敗時のリトライ ----------------------------------------------------------------

    [Fact]
    public async Task SaveSettingsAsync_書き込みに失敗し続けると指定回数リトライした上で例外を投げる()
    {
        // "|" はWindowsのファイル名として不正な文字のため、書き込みが確実に失敗し続ける。
        string invalidPath = Path.Combine(_workDir, "invalid|name.json");
        var repository = new JsonSettingsRepository(
            invalidPath, _rulesPath, maxRetryAttempts: 3, retryDelay: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<IOException>(() => repository.SaveSettingsAsync(new AppSettings()));
    }

    [Fact]
    public void Constructor_maxRetryAttemptsが0以下の場合は例外を投げる()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JsonSettingsRepository(_settingsPath, _rulesPath, maxRetryAttempts: 0));
    }

    // --- 既定の保存先パス -------------------------------------------------------------------

    [Fact]
    public void GetDefaultSettingsFilePath_LocalAppData配下のFileOrganizerフォルダを指す()
    {
        string path = JsonSettingsRepository.GetDefaultSettingsFilePath();

        Assert.Contains("FileOrganizer", path);
        Assert.EndsWith("settings.json", path);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), path);
    }

    [Fact]
    public void GetDefaultRulesFilePath_LocalAppData配下のFileOrganizerフォルダを指す()
    {
        string path = JsonSettingsRepository.GetDefaultRulesFilePath();

        Assert.Contains("FileOrganizer", path);
        Assert.EndsWith("rules.json", path);
    }
}
