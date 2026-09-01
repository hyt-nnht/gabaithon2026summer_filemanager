using System.Text.Json.Serialization;

namespace FileOrganizer.Shared.Models;

public class AppSettings
{
    // --- フォルダ監視設定 ---
    [JsonPropertyName("watch_folders")]
    public List<WatchFolderSetting> WatchFolders { get; set; } = new()
    {
        new WatchFolderSetting
        {
            Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
            Enabled = true,
            IncludeSubdirectories = false
        }
    };

    [JsonPropertyName("stability_check_interval_ms")]
    public int StabilityCheckIntervalMs { get; set; } = 750;

    [JsonPropertyName("periodic_scan_interval_hours")]
    public int PeriodicScanIntervalHours { get; set; } = 24;

    // --- ルール評価設定 ---
    [JsonPropertyName("apply_all_matching_rules")]
    public bool ApplyAllMatchingRules { get; set; } = false; // false = 上位優先ルール1件のみ実行（既定）

    // --- Quick Look プレビュー設定 ---
    [JsonPropertyName("is_quick_look_enabled")]
    public bool IsQuickLookEnabled { get; set; } = true;

    [JsonPropertyName("quick_look_shortcut")]
    public string QuickLookShortcut { get; set; } = "Space";

    // --- Python / AI 連携設定 ---
    [JsonPropertyName("python_port")]
    public int PythonPort { get; set; } = 0; // 0 = エフェメラルポート動的割当

    [JsonPropertyName("use_preloaded_slm_model")]
    public bool UsePreloadedSlmModel { get; set; } = false; // デモ用事前配置モデル認識フラグ

    [JsonPropertyName("slm_model_path")]
    public string SlmModelPath { get; set; } = string.Empty;

    // --- 通知・ログ設定 ---
    [JsonPropertyName("enable_toast_notifications")]
    public bool EnableToastNotifications { get; set; } = true;

    [JsonPropertyName("wal_checkpoint_interval_minutes")]
    public int WalCheckpointIntervalMinutes { get; set; } = 60;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;
}

public class WatchFolderSetting
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("include_subdirectories")]
    public bool IncludeSubdirectories { get; set; } = false;
}
