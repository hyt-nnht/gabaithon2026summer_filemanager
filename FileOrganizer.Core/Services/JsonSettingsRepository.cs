using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Services;

/// <summary>
/// <see cref="ISettingsRepository"/>の実装。AI_IMPLEMENTATION_GUIDE.md §1.3準拠の
/// <see cref="AppSettings"/>を<c>settings.json</c>へ、<see cref="RuleModel"/>のリストを
/// <c>rules.json</c>へ、それぞれ<see cref="System.Text.Json"/>で非同期にシリアライズ/デシリアライズする。
/// </summary>
/// <remarks>
/// <para>
/// <b>保存の堅牢性</b>: 各保存は「一時ファイルへ書き込み → <see cref="File.Replace(string, string, string)"/>で
/// 本ファイルへ原子的に置き換え（既存の本ファイルは自動的に<c>.bak</c>へ退避）」という手順で行う。
/// 途中でプロセスが強制終了しても、本ファイルは「更新前の完全な内容」か「更新後の完全な内容」の
/// いずれかであり続け、破損した中間状態になることはない。書き込みが一時的なI/Oエラー
/// （他プロセスによる排他ロック等）で失敗した場合は、指定回数まで間隔を空けて自動リトライする。
/// </para>
/// <para>
/// <b>読み込みの堅牢性</b>: 本ファイルの読み込みに失敗（破損・アクセス不能）した場合は、直前の保存で
/// 作られた<c>.bak</c>バックアップからの復旧を試みる。
/// </para>
/// <para>
/// <b>プリセット復元</b>: 仕様書冒頭「ノーコードGUI」の「内部で<c>Rules.json</c>を自動生成・プリセット同梱」に
/// 従い、<c>rules.json</c>が存在しない初回起動時は<see cref="CreatePresetRules"/>の内容を自動生成・保存する。
/// <see cref="RestorePresetRulesAsync"/>はユーザーが明示的に「プリセットへ戻す」操作をした場合の
/// エントリポイントで、現在の<c>rules.json</c>をプリセット内容で上書きする（ユーザー編集分はこの操作では
/// 保持しない＝文字どおりの「復元」）。
/// </para>
/// </remarks>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    public const int DefaultMaxRetryAttempts = 3;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(200);
    private const string BackupExtension = ".bak";
    private const string TempExtension = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsFilePath;
    private readonly string _rulesFilePath;
    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _retryDelay;

    // 設定/ルールの保存を直列化する（同時多重保存によるtmp/bakファイルの競合を防ぐ）。
    // Load側はFile.Replace/File.Moveによる原子的な置き換えにより、書き込み中でも常に
    // 完全な旧内容か完全な新内容のどちらかを読むため、Load側には掛けない。
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="settingsFilePath">settings.jsonの保存先パス。省略時は<see cref="GetDefaultSettingsFilePath"/>。</param>
    /// <param name="rulesFilePath">rules.jsonの保存先パス。省略時は<see cref="GetDefaultRulesFilePath"/>。</param>
    /// <param name="maxRetryAttempts">保存失敗時の最大試行回数（初回含む）。既定3回。</param>
    /// <param name="retryDelay">リトライ間隔。既定200ms。</param>
    public JsonSettingsRepository(
        string? settingsFilePath = null,
        string? rulesFilePath = null,
        int maxRetryAttempts = DefaultMaxRetryAttempts,
        TimeSpan? retryDelay = null)
    {
        if (maxRetryAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetryAttempts), maxRetryAttempts, "リトライ回数は正の値である必要があります。");

        _settingsFilePath = settingsFilePath ?? GetDefaultSettingsFilePath();
        _rulesFilePath = rulesFilePath ?? GetDefaultRulesFilePath();
        _maxRetryAttempts = maxRetryAttempts;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    /// <summary>既定の保存先（<c>%LocalAppData%\FileOrganizer\settings.json</c>）。</summary>
    public static string GetDefaultSettingsFilePath() => Path.Combine(GetDefaultAppDataDirectory(), "settings.json");

    /// <summary>既定の保存先（<c>%LocalAppData%\FileOrganizer\rules.json</c>）。</summary>
    public static string GetDefaultRulesFilePath() => Path.Combine(GetDefaultAppDataDirectory(), "rules.json");

    private static string GetDefaultAppDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer");

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        var loaded = await LoadJsonAsync<AppSettings>(_settingsFilePath, ct).ConfigureAwait(false);
        return loaded ?? new AppSettings(); // settings.json未存在時はAppSettingsの既定値を返す
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SaveJsonWithRetryAsync(_settingsFilePath, settings, ct);
    }

    public async Task<List<RuleModel>> LoadRulesAsync(CancellationToken ct = default)
    {
        var loaded = await LoadJsonAsync<List<RuleModel>>(_rulesFilePath, ct).ConfigureAwait(false);
        if (loaded != null)
        {
            return loaded;
        }

        // rules.json未存在（初回起動）→ 仕様書「内部でRules.jsonを自動生成・プリセット同梱」に従い
        // プリセットから自動生成して保存し、そのまま返す。
        var presets = CreatePresetRules();
        await SaveJsonWithRetryAsync(_rulesFilePath, presets, ct).ConfigureAwait(false);
        return presets;
    }

    public Task SaveRulesAsync(List<RuleModel> rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return SaveJsonWithRetryAsync(_rulesFilePath, rules, ct);
    }

    public Task RestorePresetRulesAsync(CancellationToken ct = default)
        => SaveJsonWithRetryAsync(_rulesFilePath, CreatePresetRules(), ct);

    // --- JSON読み込み（バックアップへのフォールバック付き） -------------------------------------

    private static async Task<T?> LoadJsonAsync<T>(string filePath, CancellationToken ct) where T : class
    {
        if (File.Exists(filePath))
        {
            try
            {
                return await ReadJsonFileAsync<T>(filePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 本ファイルが破損・読み取り不能 → バックアップからの復旧を試みる。
            }
        }

        string backupPath = filePath + BackupExtension;
        if (File.Exists(backupPath))
        {
            try
            {
                return await ReadJsonFileAsync<T>(backupPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // バックアップも読めない → 呼び出し元で既定値へのフォールバックに委ねる。
            }
        }

        return null;
    }

    private static async Task<T> ReadJsonFileAsync<T>(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        return result ?? throw new JsonException($"デシリアライズ結果がnullでした: {filePath}");
    }

    // --- JSON保存（一時ファイル経由の原子的置き換え + リトライ + .bakバックアップ） -------------------

    private async Task SaveJsonWithRetryAsync<T>(string filePath, T value, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? lastException = null;
            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WriteJsonAtomicallyAsync(filePath, value, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 一時的なI/Oエラー（他プロセスによる排他ロック・ウイルススキャン等）とみなしリトライする。
                    lastException = ex;
                    if (attempt < _maxRetryAttempts)
                    {
                        await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
                    }
                }
            }

            throw new IOException(
                $"設定/ルールの保存に{_maxRetryAttempts}回失敗しました: {filePath}", lastException);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string filePath, T value, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = filePath + TempExtension;
        string backupPath = filePath + BackupExtension;

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        if (File.Exists(filePath))
        {
            // 本ファイル → .bak へ退避しつつ、tmp → 本ファイルへ原子的に置き換える。
            File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            // 初回保存（本ファイルがまだ存在しない）→ 単純にリネームするだけでよい。
            File.Move(tempPath, filePath);
        }
    }

    // --- プリセットルール -----------------------------------------------------------------

    /// <summary>
    /// 同梱プリセットルール一覧。拡張子ベースの基本的な整理ルールを一式提供し、
    /// 初回起動時の<c>rules.json</c>自動生成、および<see cref="RestorePresetRulesAsync"/>で使用する。
    /// </summary>
    private static List<RuleModel> CreatePresetRules()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        return new List<RuleModel>
        {
            new()
            {
                Name = "PDFをドキュメントへ整理",
                Enabled = true,
                Conditions = new List<RuleCondition>
                {
                    new() { Type = "extension", Operator = "equals", Value = "pdf" },
                },
                Actions = new List<RuleAction>
                {
                    new() { Type = "move", Destination = Path.Combine(documents, "Organized", "PDF") },
                },
            },
            new()
            {
                Name = "画像をピクチャへ整理",
                Enabled = true,
                Conditions = new List<RuleCondition>
                {
                    new() { Type = "extension", Operator = "in", Value = new[] { "jpg", "jpeg", "png", "gif", "bmp" } },
                },
                Actions = new List<RuleAction>
                {
                    new() { Type = "move", Destination = Path.Combine(pictures, "Organized") },
                },
            },
            new()
            {
                Name = "動画をビデオへ整理",
                Enabled = true,
                Conditions = new List<RuleCondition>
                {
                    new() { Type = "extension", Operator = "in", Value = new[] { "mp4", "mov", "avi", "mkv" } },
                },
                Actions = new List<RuleAction>
                {
                    new() { Type = "move", Destination = Path.Combine(videos, "Organized") },
                },
            },
            new()
            {
                Name = "圧縮ファイルをドキュメントへ整理",
                Enabled = true,
                Conditions = new List<RuleCondition>
                {
                    new() { Type = "extension", Operator = "in", Value = new[] { "zip", "rar", "7z" } },
                },
                Actions = new List<RuleAction>
                {
                    new() { Type = "move", Destination = Path.Combine(documents, "Organized", "Archives") },
                },
            },
        };
    }
}
