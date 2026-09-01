using System.Text.Json.Serialization;

namespace FileOrganizer.Shared.Models;

public class RuleModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("watch_folder")]
    public string WatchFolder { get; set; } = string.Empty;

    [JsonPropertyName("conditions")]
    public List<RuleCondition> Conditions { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<RuleAction> Actions { get; set; } = new();
}

public class RuleCondition
{
    // "extension", "filename", "size_mb", "days_old", "ocr_contains", "ai_category"
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    // "equals", "contains", "regex", "greater_than", "less_than", "in"
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "equals";

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

public class RuleAction
{
    // "move", "copy", "rename", "recycle"
    [JsonPropertyName("type")]
    public string Type { get; set; } = "move";

    // "destination" (move/copy用), "pattern" (rename用)
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }
}
