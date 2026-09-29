using System.Globalization;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Automation;

/// <summary>One setting of a trigger, condition or action (drives the editor and validation).</summary>
public sealed record FieldSpec(string Key, FieldKind Kind, bool Required = true, string[]? Choices = null, string? Default = null)
{
    /// <summary>"repeat=weekly": the field only applies (and is only shown) when another field has that value.</summary>
    public string? ShowWhen { get; init; }

    public bool AppliesTo(AutomationStep step) =>
        ShowWhen is not { } rule || rule.Split('=', 2) is [var key, var value] && string.Equals(step.Get(key), value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What every registered step type declares: id, category, fields and validation.</summary>
public abstract class StepDescriptor(string id, string category, string icon, params FieldSpec[] fields)
{
    public string Id { get; } = id;
    public string Category { get; } = category;
    public string Icon { get; } = icon;
    public IReadOnlyList<FieldSpec> Fields { get; } = fields;

    /// <summary>A resource key describing the first problem, or null when the settings are valid.</summary>
    public virtual string? Validate(AutomationStep step)
    {
        foreach (var f in Fields)
        {
            var v = step.Get(f.Key);
            if (v == null) { if (f.Required) return "Auto_ErrRequired"; continue; }
            var ok = f.Kind switch
            {
                FieldKind.Number => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                FieldKind.Time => TimeOnly.TryParseExact(v, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                FieldKind.Date => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                FieldKind.Days => AutomationText.ParseDays(v) != WeekdayMask.None,
                FieldKind.Choice => f.Choices == null || f.Choices.Contains(v),
                FieldKind.Game or FieldKind.Project or FieldKind.Trip => v == "any" || int.TryParse(v, out _),
                FieldKind.Executable => AutomationText.IsExecutableName(v),
                FieldKind.Uri => EntityRef.TryParse(v, out _),
                FieldKind.Folder => Path.IsPathFullyQualified(v),
                _ => v.Length <= 2000,
            };
            if (!ok) return "Auto_ErrInvalid";
        }
        return null;
    }
}

public sealed class TriggerDescriptor(string id, string category, string icon, params FieldSpec[] fields) : StepDescriptor(id, category, icon, fields)
{
    /// <summary>Does this event match the trigger's settings? (Default: every event of the type.)</summary>
    public Func<AutomationStep, AutomationEvent, bool> Matches { get; init; } = (_, _) => true;
    /// <summary>A plausible simulated event for TEST (clearly marked as simulated).</summary>
    public Func<AutomationStep, DateTime, AutomationEvent> Sample { get; init; } = (s, _) => new AutomationEvent(s.Type, new Dictionary<string, string>(), Simulated: true);
}

public sealed class ConditionDescriptor(string id, string category, string icon, params FieldSpec[] fields) : StepDescriptor(id, category, icon, fields)
{
    public required Func<AutomationStep, AutomationEvent, IAutomationHost, bool> Evaluate { get; init; }
}

public sealed class ActionDescriptor(string id, string category, string icon, ActionSafety safety, params FieldSpec[] fields) : StepDescriptor(id, category, icon, fields)
{
    public ActionSafety Safety { get; } = safety;
    /// <summary>Quick, reliable actions that may run while Arcadia is closing (everything else is skipped then).</summary>
    public bool AllowedOnExit { get; init; }
    /// <summary>Runs the action; returns a short note for the activity log. Throws on failure.</summary>
    public required Func<AutomationStep, AutomationEvent, IAutomationHost, CancellationToken, Task<string?>> Execute { get; init; }
}

/// <summary>
/// The capabilities automation actions and conditions may use. Implemented by the app over existing services — there is
/// deliberately no member that deletes, kills, edits the registry or runs a command line.
/// </summary>
public interface IAutomationHost
{
    DateTime Now { get; }
    string T(string key, params object?[] args);

    bool IsGamingMode { get; }
    void SetGamingMode(bool on, string reason);
    bool IsProcessRunning(string executable);
    /// <summary>True on mains power, false on battery, null when unknown (desktops without a battery report true).</summary>
    bool? IsOnAcPower { get; }
    bool IsNetworkAvailable { get; }

    Task NotifyAsync(string category, string title, string text, EntityRef? target = null);
    Task OpenSectionAsync(string section, string? tab);
    Task OpenEntityAsync(EntityRef target);
    Task OpenQuickCaptureAsync(string kind, string? text);
    /// <summary>Asks the user (Confirm-level actions). False when declined or when nobody can be asked (minimized, gaming).</summary>
    Task<bool> ConfirmAsync(string message);
    Task OpenPathAsync(string path, bool revealFolder);
    Task<string> AddDocumentAsync(string path, int? projectId, CancellationToken ct);

    Task<int> CreateTaskAsync(string title, DateOnly? due, EntityRef? context, CancellationToken ct);
    Task<int> CreateNoteAsync(string title, string text, EntityRef? context, CancellationToken ct);
    Task SetGameStatusPlayingAsync(int gameId, CancellationToken ct);
    Task ExpectGameAsync(int gameId, CancellationToken ct);
    Task StopGameSessionAsync(CancellationToken ct);
    Task StartWorkSessionAsync(int projectId, bool automatic, CancellationToken ct);
    Task StopWorkSessionAsync(CancellationToken ct);
    Task SetProjectStatusAsync(int projectId, string status, CancellationToken ct);
    Task<string> PingAsync(string host, CancellationToken ct);
}

/// <summary>All trigger, condition and action types. New modules add descriptors here (or call Add*) — no switch statements.</summary>
public sealed class AutomationRegistry
{
    private readonly Dictionary<string, TriggerDescriptor> _triggers = [];
    private readonly Dictionary<string, ConditionDescriptor> _conditions = [];
    private readonly Dictionary<string, ActionDescriptor> _actions = [];

    public IEnumerable<TriggerDescriptor> Triggers => _triggers.Values;
    public IEnumerable<ConditionDescriptor> Conditions => _conditions.Values;
    public IEnumerable<ActionDescriptor> Actions => _actions.Values;

    public void Add(TriggerDescriptor d) => _triggers.Add(d.Id, d);
    public void Add(ConditionDescriptor d) => _conditions.Add(d.Id, d);

    /// <summary>Restricted actions are refused at registration: automation never gets them.</summary>
    public void Add(ActionDescriptor d)
    {
        if (d.Safety == ActionSafety.Restricted) throw new InvalidOperationException($"Restricted action '{d.Id}' cannot be registered for automation.");
        _actions.Add(d.Id, d);
    }

    public TriggerDescriptor? Trigger(string id) => _triggers.GetValueOrDefault(id);
    public ConditionDescriptor? Condition(string id) => _conditions.GetValueOrDefault(id);
    public ActionDescriptor? Action(string id) => _actions.GetValueOrDefault(id);

    /// <summary>Every problem with a definition (resource keys with the step). Empty = valid.</summary>
    public List<string> Validate(AutomationDefinition d)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 200) errors.Add("Auto_ErrName");
        if (Trigger(d.Trigger.Type) is not { } t) errors.Add("Auto_ErrUnknownTrigger:" + d.Trigger.Type);
        else if (t.Validate(d.Trigger) is { } te) errors.Add(te + ":" + d.Trigger.Type);
        foreach (var c in d.Conditions)
            if (Condition(c.Type) is not { } cd) errors.Add("Auto_ErrUnknownCondition:" + c.Type);
            else if (cd.Validate(c) is { } ce) errors.Add(ce + ":" + c.Type);
        if (d.Actions.Count == 0) errors.Add("Auto_ErrNoActions");
        if (d.Actions.Count > 20 || d.Conditions.Count > 20) errors.Add("Auto_ErrTooMany");
        foreach (var a in d.Actions)
            if (Action(a.Type) is not { } ad) errors.Add("Auto_ErrUnknownAction:" + a.Type);
            else if (ad.Validate(a) is { } ae) errors.Add(ae + ":" + a.Type);
        return errors;
    }
}

/// <summary>Small pure helpers shared by the catalog and the editor.</summary>
public static class AutomationText
{
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>"mon,wed,fri" → mask.</summary>
    public static WeekdayMask ParseDays(string? text)
    {
        var mask = WeekdayMask.None;
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = Array.IndexOf(DayNames, part.ToLowerInvariant()[..Math.Min(3, part.Length)]);
            if (i >= 0) mask |= (WeekdayMask)(1 << i);
        }
        return mask;
    }

    public static string FormatDays(WeekdayMask mask) => string.Join(',', Enumerable.Range(0, 7).Where(i => mask.HasFlag((WeekdayMask)(1 << i))).Select(i => DayNames[i]));

    /// <summary>A file name like "Cyberpunk2077.exe" (a path is reduced to its name). No wildcards, no command lines.</summary>
    public static bool IsExecutableName(string value)
    {
        var name = ExeName(value);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length is > 4 and < 260 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public static string ExeName(string value) => Path.GetFileName(value.Trim().Trim('"')).ToLowerInvariant();

    /// <summary>"pdf, .docx" → {"pdf","docx"}.</summary>
    public static HashSet<string> Extensions(string? value) =>
        (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToHashSet();

    /// <summary>Replaces {game}, {file}, {minutes}… with event data (unknown placeholders stay as typed).</summary>
    public static string Fill(string text, AutomationEvent e)
    {
        var s = text;
        foreach (var (k, v) in e.Data) s = s.Replace("{" + k + "}", v, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    public static bool InWindow(TimeOnly now, TimeOnly from, TimeOnly to) => from <= to ? now >= from && now < to : now >= from || now < to;
}
