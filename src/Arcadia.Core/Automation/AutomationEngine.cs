using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Arcadia.Core.Organizer;
using Microsoft.Extensions.Logging;

namespace Arcadia.Core.Automation;

/// <summary>
/// WHEN → IF → THEN. Receives events from trigger sources, finds the enabled automations whose trigger matches, applies
/// duplicate suppression, cooldown, once-per-occurrence keys and loop prevention, evaluates the conditions (ALL / ANY),
/// runs the actions in order through <see cref="IAutomationHost"/> and records every run. One failing automation never
/// affects another. Deterministic: no AI, no network (except a user's own ping action), no scripting.
/// </summary>
public sealed class AutomationEngine(AutomationRegistry registry, IAutomationStore store, IAutomationHost host, TimeProvider time, ILogger logger)
{
    /// <summary>Automations started by other automations: at most this many levels, and never the same automation twice.</summary>
    public const int MaxChainDepth = 4;
    /// <summary>The same automation for the same event data within this window is a duplicate (Windows repeats events).</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);

    private static readonly AsyncLocal<ImmutableChain?> Chain = new();
    private readonly Dictionary<string, DateTime> _recent = [];
    private readonly Dictionary<int, DateTime> _lastRun = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<AutomationDefinition> _definitions = [];

    public AutomationRegistry Registry => registry;
    public IReadOnlyList<AutomationDefinition> Definitions => _definitions;
    public bool IsEnabled { get; set; } = true;
    public bool IsPaused { get; set; }
    public int SuppressedDuplicates { get; private set; }
    public int BlockedLoops { get; private set; }

    /// <summary>Raised after every recorded run (UI refresh, tray, Oracle).</summary>
    public event Action<AutomationRunRecord>? RunRecorded;

    private sealed record ImmutableChain(IReadOnlyList<int> Ids)
    {
        public ImmutableChain Add(int id) => new([.. Ids, id]);
    }

    public async Task ReloadAsync(CancellationToken ct = default) => _definitions = await store.ListAsync(ct).ConfigureAwait(false);

    /// <summary>The trigger types enabled automations need (sources subscribe only to these).</summary>
    public IReadOnlySet<string> ActiveTriggerTypes() =>
        IsEnabled && !IsPaused ? _definitions.Where(d => d.Enabled).Select(d => d.Trigger.Type).ToHashSet() : new HashSet<string>();

    public IReadOnlyList<AutomationDefinition> ActiveFor(string triggerType) =>
        IsEnabled && !IsPaused ? _definitions.Where(d => d.Enabled && d.Trigger.Type == triggerType).ToList() : [];

    /// <summary>Handles an event: every matching enabled automation runs (or is skipped/blocked with a reason).</summary>
    public async Task<IReadOnlyList<AutomationRunRecord>> RaiseAsync(AutomationEvent e, CancellationToken ct = default)
    {
        if (!IsEnabled || IsPaused) return [];
        var results = new List<AutomationRunRecord>();
        foreach (var def in _definitions.Where(d => d.Enabled && d.Trigger.Type == e.Type).ToList())
        {
            try
            {
                if (registry.Trigger(def.Trigger.Type) is not { } trigger || !trigger.Matches(def.Trigger, e)) continue;
                if (await RunOneAsync(def, e, simulated: false, ct).ConfigureAwait(false) is { } run) results.Add(run);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automation {Id} could not be evaluated", def.Id);
            }
        }
        return results;
    }

    /// <summary>TEST in the editor: runs the automation against a simulated event (recorded and labelled as a simulation).</summary>
    public async Task<AutomationRunRecord> TestAsync(AutomationDefinition def, CancellationToken ct = default)
    {
        var trigger = registry.Trigger(def.Trigger.Type) ?? throw new InvalidOperationException("Unknown trigger.");
        var sample = trigger.Sample(def.Trigger, host.Now) with { Simulated = true };
        return (await RunOneAsync(def, sample, simulated: true, ct).ConfigureAwait(false))!;
    }

    private async Task<AutomationRunRecord?> RunOneAsync(AutomationDefinition def, AutomationEvent e, bool simulated, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var chain = Chain.Value;
        var data = new Dictionary<string, string>(e.Data)
        {
            ["time"] = host.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["date"] = host.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var ev = e with { Data = data };

        if (!simulated)
        {
            // duplicates: the same automation for the same event data within a few seconds
            var dupKey = def.Id + "|" + e.Type + "|" + string.Join("|", e.Data.Where(kv => kv.Key is not ("pid" or "time")).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            lock (_gate)
            {
                foreach (var old in _recent.Where(kv => now - kv.Value > DuplicateWindow).Select(kv => kv.Key).ToList()) _recent.Remove(old);
                if (_recent.ContainsKey(dupKey)) { SuppressedDuplicates++; logger.LogDebug("Duplicate event for automation {Id} suppressed", def.Id); return null; }
                _recent[dupKey] = now;
            }
            // cooldown (not logged as a run: noisy triggers would flood the log)
            lock (_gate)
                if (def.CooldownSeconds > 0 && _lastRun.TryGetValue(def.Id, out var last) && now - last < TimeSpan.FromSeconds(def.CooldownSeconds))
                {
                    logger.LogDebug("Automation {Id} in cooldown", def.Id);
                    return null;
                }
            if (e.OnceKey != null && await store.HasRunAsync(def.Id, e.OnceKey, ct).ConfigureAwait(false)) return null;
        }

        // loop prevention: an automation started (directly or indirectly) by itself, or a chain that is too deep
        if (chain != null && (chain.Ids.Contains(def.Id) || chain.Ids.Count >= MaxChainDepth))
        {
            BlockedLoops++;
            logger.LogWarning("Automation loop prevented: {Chain} → {Id}", string.Join(" → ", chain.Ids), def.Id);
            return await RecordAsync(def, ev, RunStatus.Blocked, host.T("Auto_LoopBlocked", string.Join(" → ", chain.Ids.Append(def.Id))), 0,
                new RunDetails(data, [], [], null, "chain " + string.Join(">", chain.Ids)), simulated, ct).ConfigureAwait(false);
        }

        // conditions
        var conditionResults = new List<StepResult>();
        foreach (var c in def.Conditions)
        {
            bool ok;
            try { ok = registry.Condition(c.Type) is { } cd && cd.Evaluate(c, ev, host); }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { ok = false; }
            conditionResults.Add(new StepResult(c.Type, ok, null));
        }
        var pass = def.Conditions.Count == 0 || (def.Mode == ConditionMode.All ? conditionResults.All(r => r.Ok) : conditionResults.Any(r => r.Ok));
        if (!pass)
            return await RecordAsync(def, ev, RunStatus.Skipped, host.T("Auto_ConditionsFalse"), 0, new RunDetails(data, conditionResults, [], null, null), simulated, ct).ConfigureAwait(false);

        // run: recorded as Running first, so a crash leaves an Incomplete record and nothing is ever re-run on startup
        lock (_gate) _lastRun[def.Id] = now;
        var runId = await store.BeginRunAsync(new AutomationRunEntity
        {
            AutomationId = def.Id > 0 ? def.Id : null, AutomationName = def.Name, TriggerType = e.Type, EventKey = e.OnceKey,
            StartedAtUtc = now, Status = RunStatus.Running, IsSimulation = simulated,
        }, ct).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var actionResults = new List<StepResult>();
        var status = RunStatus.Success;
        string? error = null, technical = null;
        var previous = Chain.Value;
        Chain.Value = (chain ?? new ImmutableChain([])).Add(def.Id);
        try
        {
            foreach (var a in def.Actions)
            {
                ct.ThrowIfCancellationRequested();
                if (registry.Action(a.Type) is not { } action)
                {
                    actionResults.Add(new StepResult(a.Type, false, host.T("Auto_UnknownAction")));
                    status = RunStatus.Failed; error = host.T("Auto_UnknownAction");
                    break;
                }
                if (e.Type == "app.closing" && !action.AllowedOnExit)
                {
                    actionResults.Add(new StepResult(a.Type, false, host.T("Auto_NotOnExit")));
                    continue;
                }
                if (action.Safety == ActionSafety.Confirm && !await host.ConfirmAsync(host.T("Auto_ConfirmAction", def.Name, host.T("Auto_Action_" + a.Type))).ConfigureAwait(false))
                {
                    actionResults.Add(new StepResult(a.Type, false, host.T("Auto_NotConfirmed")));
                    status = RunStatus.Blocked; error = host.T("Auto_NotConfirmed");
                    break;
                }
                try
                {
                    var note = await action.Execute(a, ev, host, ct).ConfigureAwait(false);
                    actionResults.Add(new StepResult(a.Type, true, note));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // the automation stops at its first failing action; other automations are unaffected
                    actionResults.Add(new StepResult(a.Type, false, ex.Message));
                    status = RunStatus.Failed; error = ex.Message; technical = ex.GetType().Name + ": " + ex.Message;
                    logger.LogWarning(ex, "Automation {Id} action {Action} failed", def.Id, a.Type);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Failed; error = host.T("Auto_Cancelled");
        }
        finally
        {
            Chain.Value = previous;
        }

        var summary = status switch
        {
            RunStatus.Success => host.T("Auto_RanActions", actionResults.Count(r => r.Ok)),
            _ => error ?? status.ToString(),
        };
        var details = new RunDetails(data, conditionResults, actionResults, error, technical);
        await store.CompleteRunAsync(runId, status, (int)sw.ElapsedMilliseconds, summary, JsonSerializer.Serialize(details), CancellationToken.None).ConfigureAwait(false);
        var record = new AutomationRunRecord(runId, def.Id, def.Name, e.Type, now, (int)sw.ElapsedMilliseconds, status, summary, details, simulated);
        RunRecorded?.Invoke(record);
        return record;
    }

    private async Task<AutomationRunRecord> RecordAsync(AutomationDefinition def, AutomationEvent e, RunStatus status, string summary, int ms, RunDetails details, bool simulated, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var id = await store.BeginRunAsync(new AutomationRunEntity
        {
            AutomationId = def.Id > 0 ? def.Id : null, AutomationName = def.Name, TriggerType = e.Type, StartedAtUtc = now,
            Status = status, Summary = summary, DetailsJson = JsonSerializer.Serialize(details), IsSimulation = simulated,
        }, ct).ConfigureAwait(false);
        var record = new AutomationRunRecord(id, def.Id, def.Name, e.Type, now, ms, status, summary, details, simulated);
        RunRecorded?.Invoke(record);
        return record;
    }
}

/// <summary>Next occurrence of a schedule trigger, on top of the Phase 6 recurrence rules (local wall-clock time).</summary>
public static class ScheduleMath
{
    /// <summary>The first occurrence strictly after <paramref name="afterLocal"/>, or null (a past one-off, invalid settings).</summary>
    public static DateTime? Next(AutomationStep s, DateTime afterLocal, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (s.Get("time") is not { } t || !TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
        var from = DateOnly.FromDateTime(afterLocal);
        RecurrenceRule? rule = s.Get("repeat") switch
        {
            "daily" => new RecurrenceRule(new DateOnly(2000, 1, 1), RecurrenceFrequency.Daily),
            "weekly" => new RecurrenceRule(new DateOnly(2000, 1, 2), RecurrenceFrequency.Weekly, 1, AutomationText.ParseDays(s.Get("days")) is var m && m != WeekdayMask.None ? m : WeekdayMask.Monday),
            "monthly" => new RecurrenceRule(new DateOnly(2000, 1, Math.Clamp(s.GetInt("day") ?? 1, 1, 31)), RecurrenceFrequency.Monthly),
            "once" when s.Get("date") is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                => new RecurrenceRule(date, RecurrenceFrequency.None),
            _ => null,
        };
        if (rule is not { } r) return null;
        foreach (var day in Recurrence.Dates(r, from, from.AddDays(400)))
        {
            var local = day.ToDateTime(at);
            // a time in the spring-forward gap happens at the first valid minute after it
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
            if (local > afterLocal) return local;
        }
        return null;
    }
}
