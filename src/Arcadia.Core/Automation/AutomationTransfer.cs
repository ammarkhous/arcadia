using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcadia.Core.Automation;

/// <summary>
/// Export/import of automations as versioned JSON (<c>.arcadia-automation</c>). Parsed into plain DTOs of strings only —
/// never polymorphic or type-named deserialization. Imports are validated against the registry (unknown or unregistered
/// types rejected) and always arrive <b>disabled</b>, to be reviewed before they run.
/// </summary>
public static class AutomationTransfer
{
    public const string Format = "arcadia-automation";
    public const int Version = 1;
    public const string Extension = ".arcadia-automation";
    private const int MaxBytes = 512 * 1024;

    public sealed class FileDto
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("exportedAt")] public DateTime? ExportedAt { get; set; }
        [JsonPropertyName("automations")] public List<AutomationDto>? Automations { get; set; }
    }

    public sealed class AutomationDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("trigger")] public StepDto? Trigger { get; set; }
        [JsonPropertyName("conditionMode")] public string? ConditionMode { get; set; }
        [JsonPropertyName("conditions")] public List<StepDto>? Conditions { get; set; }
        [JsonPropertyName("actions")] public List<StepDto>? Actions { get; set; }
        [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }
    }

    public sealed class StepDto
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("settings")] public Dictionary<string, string>? Settings { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 16 };

    public static string Export(IEnumerable<AutomationDefinition> definitions, DateTime nowUtc) =>
        JsonSerializer.Serialize(new FileDto
        {
            Format = Format, Version = Version, ExportedAt = nowUtc,
            Automations = definitions.Select(d => new AutomationDto
            {
                Name = d.Name, Description = d.Description, ConditionMode = d.Mode.ToString(), CooldownSeconds = d.CooldownSeconds, Tags = d.Tags,
                Trigger = Dto(d.Trigger), Conditions = d.Conditions.Select(Dto).ToList(), Actions = d.Actions.Select(Dto).ToList(),
            }).ToList(),
        }, Options);

    private static StepDto Dto(AutomationStep s) => new() { Type = s.Type, Settings = new Dictionary<string, string>(s.Settings) };

    public sealed record ImportResult(List<AutomationDefinition> Accepted, List<string> Errors);

    /// <summary>Validates a file; accepted automations are returned disabled and unsaved (Id 0).</summary>
    public static ImportResult Import(string json, AutomationRegistry registry)
    {
        var errors = new List<string>();
        if (json.Length > MaxBytes) return new([], ["Auto_ImportTooLarge"]);
        FileDto? file;
        try { file = JsonSerializer.Deserialize<FileDto>(json, Options); }
        catch (JsonException) { return new([], ["Auto_ImportMalformed"]); }
        if (file?.Format != Format) return new([], ["Auto_ImportNotArcadia"]);
        if (file.Version is < 1 or > Version) return new([], ["Auto_ImportVersion"]);

        var accepted = new List<AutomationDefinition>();
        foreach (var (dto, i) in (file.Automations ?? []).Take(200).Select((d, i) => (d, i + 1)))
        {
            if (dto.Trigger?.Type is not { } tt || dto.Actions is null)
            {
                errors.Add($"#{i}: Auto_ImportMalformed");
                continue;
            }
            var def = new AutomationDefinition(0, dto.Name ?? "", dto.Description, Enabled: false,
                Step(dto.Trigger), Enum.TryParse<ConditionMode>(dto.ConditionMode, true, out var m) ? m : ConditionMode.All,
                (dto.Conditions ?? []).Select(Step).ToList(), dto.Actions.Select(Step).ToList(), Math.Clamp(dto.CooldownSeconds, 0, 7 * 24 * 3600), dto.Tags ?? "");
            var problems = registry.Validate(def);
            if (problems.Count > 0) { errors.Add($"#{i} {dto.Name}: {string.Join(", ", problems)}"); continue; }
            accepted.Add(def);
        }
        return new(accepted, errors);
    }

    private static AutomationStep Step(StepDto? d) =>
        new(d?.Type ?? "", (d?.Settings ?? []).Where(kv => kv.Key.Length <= 40 && kv.Value.Length <= 2000).Take(20).ToDictionary(kv => kv.Key, kv => kv.Value));
}

/// <summary>What Oracle and other modules may ask of automation (implemented by the app).</summary>
public interface IAutomationService
{
    IReadOnlyList<AutomationDefinition> Automations { get; }
    bool IsPaused { get; }
    bool IsGamingMode { get; }
    Task SetPausedAsync(bool paused);
    void SetGamingMode(bool on);
    Task<IReadOnlyList<AutomationRunRecord>> RecentRunsAsync(int take, CancellationToken ct = default);
    /// <summary>Saves a new automation (callers confirm first).</summary>
    Task<AutomationDefinition> CreateAsync(AutomationDefinition definition, CancellationToken ct = default);
    AutomationRegistry Registry { get; }
}
