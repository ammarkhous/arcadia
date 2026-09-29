using System.Text.Json;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Automation;

/// <summary>How much an action may change: Safe runs unattended; Confirm asks first; Restricted is never offered to automation.</summary>
public enum ActionSafety { Safe, Confirm, Restricted }

public enum ConditionMode { All, Any }

/// <summary>Outcome of one automation run. Running is written first so a crash leaves an Incomplete record, never a rerun.</summary>
public enum RunStatus { Running, Success, Skipped, Failed, Blocked, Incomplete }

public enum FieldKind { Text, Number, Time, Days, Date, Choice, Game, Project, Trip, Folder, Executable, Extensions, Section, Bool, Uri }

/// <summary>One configured trigger, condition or action: a registered type plus its string settings (safe to store and export).</summary>
public sealed record AutomationStep(string Type, Dictionary<string, string> Settings)
{
    public AutomationStep(string type) : this(type, []) { }

    public string? Get(string key) => Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    public int? GetInt(string key) => int.TryParse(Get(key), out var v) ? v : null;
    public bool GetBool(string key) => Get(key) is "true" or "1" or "yes";

    public AutomationStep With(string key, string? value)
    {
        var s = new Dictionary<string, string>(Settings);
        if (value == null) s.Remove(key); else s[key] = value;
        return this with { Settings = s };
    }
}

/// <summary>Stored automation (EF entity). Steps are JSON of <see cref="AutomationStep"/> lists; see <see cref="AutomationDefinition"/>.</summary>
public sealed class AutomationEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public string TriggerType { get; set; } = string.Empty;
    public string TriggerJson { get; set; } = "{}";
    public ConditionMode ConditionMode { get; set; }
    public string ConditionsJson { get; set; } = "[]";
    public string ActionsJson { get; set; } = "[]";
    public int CooldownSeconds { get; set; }
    public string Tags { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public int RunCount { get; set; }
    public RunStatus? LastStatus { get; set; }
}

/// <summary>The activity log (bounded): one row per run, with a JSON detail block for the log view.</summary>
public sealed class AutomationRunEntity
{
    public int Id { get; set; }
    public int? AutomationId { get; set; }
    public string AutomationName { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    /// <summary>For once-per-occurrence triggers (a bill's due date, a trip's start…): never fire twice for the same key.</summary>
    public string? EventKey { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public int DurationMs { get; set; }
    public RunStatus Status { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public bool IsSimulation { get; set; }
}

/// <summary>A timed project work session (Phase 9). Open while <see cref="EndedAtUtc"/> is null; LastSeen bounds crash recovery.</summary>
public sealed class WorkSession
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public int Minutes { get; set; }
    public string? Note { get; set; }
    public bool IsAutomatic { get; set; }
}

public sealed record AutomationDefinition(
    int Id,
    string Name,
    string? Description,
    bool Enabled,
    AutomationStep Trigger,
    ConditionMode Mode,
    IReadOnlyList<AutomationStep> Conditions,
    IReadOnlyList<AutomationStep> Actions,
    int CooldownSeconds = 0,
    string Tags = "")
{
    public DateTime? LastRunAtUtc { get; init; }
    public int RunCount { get; init; }
    public RunStatus? LastStatus { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Plain storage shape of a step (strings only).</summary>
    private sealed class StepJson
    {
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Settings { get; set; } = [];
    }

    private static List<AutomationStep> Steps(string json) =>
        (JsonSerializer.Deserialize<List<StepJson>>(json, Json) ?? []).Select(x => new AutomationStep(x.Type, x.Settings ?? [])).ToList();

    public static AutomationDefinition From(AutomationEntity e) => new(
        e.Id, e.Name, e.Description, e.IsEnabled,
        new AutomationStep(e.TriggerType, JsonSerializer.Deserialize<Dictionary<string, string>>(e.TriggerJson) ?? []),
        e.ConditionMode,
        Steps(e.ConditionsJson),
        Steps(e.ActionsJson),
        e.CooldownSeconds, e.Tags)
    {
        LastRunAtUtc = e.LastRunAtUtc, RunCount = e.RunCount, LastStatus = e.LastStatus, CreatedAtUtc = e.CreatedAtUtc, UpdatedAtUtc = e.UpdatedAtUtc,
    };

    public void CopyTo(AutomationEntity e)
    {
        e.Name = Name.Trim();
        e.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        e.IsEnabled = Enabled;
        e.TriggerType = Trigger.Type;
        e.TriggerJson = JsonSerializer.Serialize(Trigger.Settings);
        e.ConditionMode = Mode;
        e.ConditionsJson = JsonSerializer.Serialize(Conditions.Select(c => new StepJson { Type = c.Type, Settings = c.Settings }).ToList(), Json);
        e.ActionsJson = JsonSerializer.Serialize(Actions.Select(a => new StepJson { Type = a.Type, Settings = a.Settings }).ToList(), Json);
        e.CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 7 * 24 * 3600);
        e.Tags = Tags ?? string.Empty;
    }
}

/// <summary>A run as shown in the activity log.</summary>
public sealed record AutomationRunRecord(int Id, int? AutomationId, string AutomationName, string TriggerType, DateTime StartedAtUtc, int DurationMs,
    RunStatus Status, string Summary, RunDetails Details, bool IsSimulation);

public sealed record StepResult(string Type, bool Ok, string? Note);

public sealed record RunDetails(Dictionary<string, string> Trigger, List<StepResult> Conditions, List<StepResult> Actions, string? Error, string? Technical)
{
    public static RunDetails Empty => new([], [], [], null, null);
}

/// <summary>Something that happened (or a simulated event): a trigger type plus data the conditions and actions can read.</summary>
public sealed record AutomationEvent(string Type, IReadOnlyDictionary<string, string> Data, string? OnceKey = null, bool Simulated = false)
{
    public string? Get(string key) => Data.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;
    public int? GetInt(string key) => int.TryParse(Get(key), out var v) ? v : null;

    /// <summary>The Arcadia record the event is about (game, task, project, trip…), for "open it" and context-linked notes.</summary>
    public EntityRef? Subject =>
        GetInt("game") is { } g ? new(EntityKind.Game, g)
        : GetInt("task") is { } t ? new(EntityKind.Task, t)
        : GetInt("project") is { } p ? new(EntityKind.Project, p)
        : GetInt("trip") is { } tr ? new(EntityKind.Trip, tr)
        : GetInt("event") is { } ev ? new(EntityKind.Event, ev)
        : null;
}

/// <summary>Persistence of automations and the activity log (EF in Arcadia.Data; in-memory in tests).</summary>
public interface IAutomationStore
{
    Task<IReadOnlyList<AutomationDefinition>> ListAsync(CancellationToken ct = default);
    Task<AutomationDefinition?> GetAsync(int id, CancellationToken ct = default);
    Task<AutomationDefinition> SaveAsync(AutomationDefinition definition, CancellationToken ct = default);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task<int> BeginRunAsync(AutomationRunEntity run, CancellationToken ct = default);
    Task CompleteRunAsync(int runId, RunStatus status, int durationMs, string summary, string detailsJson, CancellationToken ct = default);
    Task<bool> HasRunAsync(int automationId, string eventKey, CancellationToken ct = default);
    Task<IReadOnlyList<AutomationRunRecord>> GetRunsAsync(int take, int? automationId = null, CancellationToken ct = default);
    /// <summary>Runs left "Running" by a crash become Incomplete (never re-executed). Returns how many.</summary>
    Task<int> MarkInterruptedAsync(CancellationToken ct = default);
    Task PruneRunsAsync(int keepDays, int keepMax, CancellationToken ct = default);
}

/// <summary>Project work sessions (manual or automation-started). One open session at a time.</summary>
public interface IWorkSessionStore
{
    Task<WorkSession?> GetOpenAsync(CancellationToken ct = default);
    /// <summary>Starts a session (closing any open one first). Returns the new session.</summary>
    Task<WorkSession> StartAsync(int projectId, DateTime startUtc, bool automatic, CancellationToken ct = default);
    /// <summary>Closes the open session at <paramref name="endUtc"/>; minutes are end − start. Null when none was open.</summary>
    Task<WorkSession?> StopAsync(DateTime endUtc, string? note = null, CancellationToken ct = default);
    Task HeartbeatAsync(DateTime nowUtc, CancellationToken ct = default);
    Task<IReadOnlyList<(WorkSession Session, string Project)>> RecentAsync(int take, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, int>> MinutesByProjectAsync(CancellationToken ct = default);
}
