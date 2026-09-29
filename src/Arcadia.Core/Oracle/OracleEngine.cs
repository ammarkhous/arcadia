using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Oracle;

/// <summary>When changes ask first: Smart (creates and unclear changes preview; exact simple changes run), Always, Minimal.</summary>
public enum OracleConfirmation { Smart, Always, Minimal }

/// <summary>What the host (the app) provides: time, formatting and localized response templates.</summary>
public interface IOracleEnvironment
{
    DateTime Now { get; }
    /// <summary>"en" or "ar": the interface language, used for responses and finance category names.</summary>
    string UiLanguage { get; }
    /// <summary>A localized response template (Orc_* keys), formatted with the arguments.</summary>
    string T(string key, params object?[] args);
    string Money(decimal amount, string currency);
    string Date(DateOnly date);
    string Time(TimeOnly time);
    Finance.DecimalStyle DecimalStyle { get; }
    int? DefaultAccountId { get; }
    OracleConfirmation Confirmation { get; }
}

/// <summary>Everything a command handler may use: services, host, and the name index. No database access.</summary>
public sealed record OracleRuntime(OracleServices Services, IOracleEnvironment Env, OracleEntityIndex Index);

/// <summary>
/// One registered command: which intent/entities it serves, how safe it is, examples for help, how well it matches a parse
/// (0 = not at all, 1 = exactly) and what it does. Commands are small classes; the engine picks one by score.
/// </summary>
public abstract class OracleCommand
{
    public abstract string Id { get; }
    /// <summary>The shell section the command belongs to (help groups, suggestions per page).</summary>
    public abstract string Module { get; }
    public abstract OracleIntent Intent { get; }
    public virtual OracleEntity[] Entities => [];
    public virtual OracleSafety Safety => OracleSafety.ReadOnly;
    /// <summary>True when free words are the command's argument (a name, a title); otherwise they lower the confidence.</summary>
    public virtual bool UsesFreeText => false;
    /// <summary>True when follow-ups ("only high priority", "this week") can refine the result.</summary>
    public virtual bool CanRefine => false;
    /// <summary>Help examples; the first one is also used for suggestions.</summary>
    public abstract string[] Examples { get; }
    public virtual string[] ExamplesDe => [];
    public virtual string[] ExamplesAr => [];

    public abstract double Match(OracleParse p, OracleContext context);
    public abstract Task<OracleResult> ExecuteAsync(OracleCall call, CancellationToken ct);

    public string[] ExamplesFor(OracleLanguage language) => language switch
    {
        OracleLanguage.German when ExamplesDe.Length > 0 => ExamplesDe,
        OracleLanguage.Arabic when ExamplesAr.Length > 0 => ExamplesAr,
        _ => Examples,
    };
}

/// <summary>A command being executed: the parse, context, runtime and small helpers for building results.</summary>
public sealed class OracleCall(OracleEngine engine, OracleCommand command, OracleParse parse, OracleContext context, OracleRuntime runtime, double score)
{
    public OracleEngine Engine { get; } = engine;
    public OracleCommand Command { get; } = command;
    public OracleParse P { get; } = parse;
    public OracleContext Context { get; } = context;
    public OracleRuntime Runtime { get; } = runtime;
    public OracleServices S => Runtime.Services;
    public IOracleEnvironment Env => Runtime.Env;
    public double Score { get; } = score;
    public DateTime Now => Env.Now;
    public DateOnly Today => DateOnly.FromDateTime(Env.Now);
    public List<string> Notes { get; } = [];

    public string T(string key, params object?[] args) => Env.T(key, args);

    public Task<OracleResolution> ResolveAsync(string? text, CancellationToken ct, params EntityKind[] kinds) => Runtime.Index.ResolveAsync(text, kinds, ct);

    /// <summary>The record "it"/"this"/an implicit target refers to, if it is one of the kinds.</summary>
    public (EntityRef Ref, string? Name)? Referent(params EntityKind[] kinds) =>
        Context.Referent is { } r && (kinds.Length == 0 || kinds.Contains(r.Ref.Kind)) ? r : null;

    /// <summary>
    /// Runs a change or shows its preview first, depending on the confirmation setting: previews for creates (unless
    /// Minimal) and for updates on anything but an exact, single match (unless Minimal); Always previews everything.
    /// </summary>
    public async Task<OracleResult> ChangeAsync(OraclePreview preview, bool exact, CancellationToken ct, bool alwaysConfirm = false)
    {
        var level = Env.Confirmation;
        var ask = alwaysConfirm || level == OracleConfirmation.Always
                  || (level == OracleConfirmation.Smart && (Command.Safety == OracleSafety.Create || !exact));
        foreach (var n in Notes) if (!preview.Notes.Contains(n)) preview.Notes.Add(n);
        if (ask || preview.Confirm.Invoke == null)
            return new OracleResult { Kind = OracleResultKind.Preview, Headline = preview.Title, Preview = preview, Confidence = exact ? OracleConfidence.High : OracleConfidence.Medium };
        return await preview.Confirm.Invoke(ct).ConfigureAwait(false);
    }

    public OracleAction ConfirmAction(Func<CancellationToken, Task<OracleResult>> apply) =>
        new() { Label = T("Orc_Confirm"), Icon = "Check", Kind = OracleActionKind.Confirm, Invoke = apply, IsPrimary = true };

    /// <summary>"Which one?" for several plausible records: each choice re-runs the command with the exact name.</summary>
    public OracleResult Choose(OracleResolution r, Func<OracleCandidate, string> commandFor, string? headline = null) => new()
    {
        Kind = OracleResultKind.Clarify,
        Headline = headline ?? (r.IsWeak ? T("Orc_DidYouMean") : T("Orc_WhichOne", r.Query)),
        Confidence = OracleConfidence.Medium,
        Items = r.Candidates.Select(c => new OracleItem
        {
            Title = c.Name,
            Subtitle = KindLabel(c.Ref.Kind),
            Icon = OracleIcons.Of(c.Ref.Kind),
            Ref = c.Ref,
            Actions = [new OracleAction { Label = T("Orc_Choose"), Icon = "Check", Kind = OracleActionKind.Run, Command = commandFor(c), IsPrimary = true }],
        }).ToList(),
    };

    public OracleResult NotFound(string what) => new()
    {
        Kind = OracleResultKind.Message,
        Headline = T("Orc_NotFound", what),
        Confidence = OracleConfidence.Medium,
        Suggestions = [$"search {what}"],
    };

    public string KindLabel(EntityKind kind) => T("Orc_Kind_" + kind);

    public string Quote(string name) => name.Contains(' ') || name.Contains('"') ? "\"" + name.Replace("\"", "") + "\"" : name;
}

/// <summary>Stages 7–12: context, matching, confidence, plan, execution, trace. One engine per app, used on the UI thread.</summary>
public sealed class OracleEngine
{
    private readonly List<OracleCommand> _commands;
    private readonly OracleRuntime _runtime;

    public OracleEngine(IEnumerable<OracleCommand> commands, OracleRuntime runtime)
    {
        _commands = commands.ToList();
        _runtime = runtime;
    }

    public IReadOnlyList<OracleCommand> Commands => _commands;
    public OracleRuntime Runtime => _runtime;

    public async Task<OracleResult> RunAsync(OracleRequest request, OracleContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var env = _runtime.Env;
        var now = env.Now;
        context.Expire(now.ToUniversalTime());
        var uiLang = env.UiLanguage == "ar" ? OracleLanguage.Arabic : OracleLanguage.English;
        var parse = OracleParser.Parse(request.Text, DateOnly.FromDateTime(now), uiLang, request.Language);
        var parseMs = sw.Elapsed.TotalMilliseconds;

        OracleCommand? command = null;
        var score = 0.0;
        OracleResult result;
        try
        {
            if (parse.Input.Tokens.Count == 0 && parse.Payload == null)
                result = Suggest(context, parse.Language);
            else if (parse.Verb == "refuse" || (parse.Verb == "off" && !parse.Has("mode:gaming") && !parse.Has("mode:powersaver") && parse.Entity != OracleEntity.Automation))
                result = Refuse();
            else
            {
                // a follow-up refines the last list ("only high priority", "this week")
                if (context.Last is { } last && IsRefinement(parse) && _commands.FirstOrDefault(c => c.Id == last.CommandId) is { } again)
                {
                    var merged = Merge(last.Parse, parse);
                    var s = again.Match(merged, context);
                    if (s > 0) { parse = merged; command = again; score = Math.Min(1, s); }
                }
                if (command == null) (command, score, _) = Pick(parse, context);

                if (command == null || score < 0.35)
                    result = Unknown(context, parse.Language);
                else
                {
                    result = await command.ExecuteAsync(new OracleCall(this, command, parse, context, _runtime, score), ct).ConfigureAwait(false);
                    var fromScore = score >= 0.75 ? OracleConfidence.High : score >= 0.5 ? OracleConfidence.Medium : OracleConfidence.Low;
                    if (fromScore > result.Confidence) result.Confidence = fromScore; // enum: High=0 < Medium < Low
                    if (result.Kind == OracleResultKind.Unknown) command = null;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new OracleResult { Kind = OracleResultKind.Error, Headline = env.T("Orc_Failed"), Detail = ex.Message, Confidence = OracleConfidence.Low };
        }

        // context: what the result was about, and the query follow-ups refine
        var nowUtc = now.ToUniversalTime();
        if (result.Focus is { } focus) context.SetFocus(focus, result.FocusName, nowUtc);
        if (command is { CanRefine: true } && result.Kind is OracleResultKind.List or OracleResultKind.Metric)
            context.SetLast(new OracleFollowUp(command.Id, parse, context.Module), nowUtc);
        else if (result.Kind is OracleResultKind.Navigated or OracleResultKind.Refused or OracleResultKind.Unknown)
            context.SetLast(null, nowUtc);

        result.Trace = new OracleTrace(
            request.Text,
            parse.Input.Normalized,
            parse.Language,
            string.Join(' ', parse.Concepts),
            command?.Intent.ToString() ?? "-",
            parse.Entities.Count > 0 ? string.Join(',', parse.Entities) : "-",
            parse.Describe(),
            $"module={context.Module}; page={context.CurrentName ?? "-"}; focus={context.FocusName ?? "-"}; last={context.Last?.CommandId ?? "-"}",
            result.Confidence,
            score.ToString("0.00", CultureInfo.InvariantCulture),
            command?.Id ?? "-",
            Math.Round(parseMs, 2),
            Math.Round(sw.Elapsed.TotalMilliseconds, 2));
        return result;
    }

    /// <summary>Scores every command; free words a command does not use and typo-corrected vocabulary lower the score.</summary>
    public (OracleCommand? Best, double Score, OracleCommand? RunnerUp) Pick(OracleParse parse, OracleContext context)
    {
        OracleCommand? best = null, second = null;
        double bestScore = 0, secondScore = 0;
        foreach (var c in _commands)
        {
            var s = c.Match(parse, context);
            if (s <= 0) continue;
            if (!c.UsesFreeText && parse.FreeWordCount > 0) s -= Math.Min(0.3, 0.1 * parse.FreeWordCount);
            if (parse.UsedFuzzyVocabulary) s -= 0.1;
            if (s > bestScore) { second = best; secondScore = bestScore; best = c; bestScore = s; }
            else if (s > secondScore) { second = c; secondScore = s; }
        }
        return (best, Math.Min(1, bestScore), second);
    }

    private static readonly HashSet<string> StatusFilters =
        ["unfinished", "completed", "backlog", "playing", "abandoned", "active", "onhold", "planning", "visited", "wanted", "notplayed", "missing", "overdue", "upcoming"];

    public static bool IsRefinement(OracleParse p) =>
        p.Verb == null && p.Module == null && p.Tool == null && p.Metric == null && !p.RefersBack && p.Question is null or "what"
        && (p.Entities.Count == 0 || p.IsFollowUp)
        && (p.Filters.Count > 0 || p.Period != null || p.Topic != null || (p.IsFollowUp && p.Subject.Length > 0));

    /// <summary>The last query with the follow-up's filters, period and topic applied (a new status filter replaces the old one).</summary>
    public static OracleParse Merge(OracleParse last, OracleParse now)
    {
        var m = new OracleParse { Input = now.Input, Language = now.Language };
        m.Concepts.AddRange(last.Concepts);
        m.Concepts.AddRange(now.Concepts);
        m.Verb = last.Verb;
        m.Question = last.Question;
        m.Entities.AddRange(last.Entities);
        foreach (var e in now.Entities) if (!m.Entities.Contains(e)) m.Entities.Add(e);
        var replaceStatus = now.Filters.Any(StatusFilters.Contains);
        foreach (var f in last.Filters) if (!(replaceStatus && StatusFilters.Contains(f))) m.Filters.Add(f);
        foreach (var f in now.Filters) m.Filters.Add(f);
        m.Module = last.Module;
        m.Tool = last.Tool;
        m.Metric = last.Metric;
        m.Period = now.Period ?? last.Period;
        m.Time = last.Time;
        m.Subject = last.Subject;
        m.Target = last.Target;
        m.TargetKind = last.TargetKind;
        m.Topic = now.Topic ?? (now.IsFollowUp && now.Subject.Length > 0 ? now.Subject : last.Topic);
        m.Tagged = last.Tagged || now.Tagged;
        m.AllRelated = last.AllRelated;
        m.IsFollowUp = true;
        m.VocabularyHits = last.VocabularyHits + now.VocabularyHits;
        return m;
    }

    private OracleResult Refuse() => new()
    {
        Kind = OracleResultKind.Refused,
        Headline = _runtime.Env.T("Orc_Refused"),
        Detail = _runtime.Env.T("Orc_RefusedDetail"),
        Confidence = OracleConfidence.High,
    };

    /// <summary>"I couldn't match that": suggestions from the current module plus a search action.</summary>
    public OracleResult Unknown(OracleContext context, OracleLanguage language, string? searchText = null) => new()
    {
        Kind = OracleResultKind.Unknown,
        Headline = _runtime.Env.T("Orc_Unknown"),
        Detail = _runtime.Env.T("Orc_UnknownDetail"),
        Confidence = OracleConfidence.Low,
        Suggestions = SuggestionsFor(context.Module, language, 4),
    };

    /// <summary>Empty input: a few commands for the current page, then general ones.</summary>
    public OracleResult Suggest(OracleContext context, OracleLanguage language) => new()
    {
        Kind = OracleResultKind.Help,
        Headline = _runtime.Env.T("Orc_Ready"),
        Confidence = OracleConfidence.High,
        Suggestions = SuggestionsFor(context.Module, language, 6),
    };

    public List<string> SuggestionsFor(string module, OracleLanguage language, int take)
    {
        var local = _commands.Where(c => c.Module == module).Select(c => c.ExamplesFor(language)[0]);
        var general = _commands.Where(c => c.Module != module && c.Intent is OracleIntent.Show or OracleIntent.Calculate).Select(c => c.ExamplesFor(language)[0]);
        return local.Concat(general).Distinct().Take(take).ToList();
    }
}

/// <summary>A record that may be what the user named, with how well its name matched.</summary>
public sealed record OracleCandidate(EntityRef Ref, string Name, double Score);

/// <summary>
/// The result of resolving a name: one strong match (act on it), several (ask), only plausible ones (offer, never act),
/// or none. Changes are only made on <see cref="Single"/>.
/// </summary>
public sealed class OracleResolution
{
    public required string Query { get; init; }
    public List<OracleCandidate> Candidates { get; init; } = [];
    public OracleCandidate? Single { get; init; }
    /// <summary>The single match is an exact (or prefix-exact) name, safe for direct changes.</summary>
    public bool IsExact => Single is { Score: >= 0.95 };
    public bool IsWeak => Single == null && Candidates.Count > 0 && Candidates[0].Score < OracleFuzzy.Strong;
    public bool IsNone => Candidates.Count == 0;
}

/// <summary>
/// Small in-memory name lists for resolving names (games, projects, trips, places, accounts, goals, bills). Loaded lazily on
/// first use, dropped when the app reports a change (and after two minutes). Large sets (tasks, notes, documents, events)
/// are searched on demand through the loader instead of cached.
/// </summary>
public sealed class OracleEntityIndex(Func<EntityKind, string?, CancellationToken, Task<IReadOnlyList<(EntityRef Ref, string Name)>>> loader, Func<DateTime>? clock = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly HashSet<EntityKind> Cached =
        [EntityKind.Game, EntityKind.Project, EntityKind.Trip, EntityKind.Place, EntityKind.Account, EntityKind.Goal, EntityKind.Recurring, EntityKind.Budget];

    private readonly ConcurrentDictionary<EntityKind, (DateTime At, Task<IReadOnlyList<(EntityRef Ref, string Name)>> Names)> _cache = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public int CachedKinds => _cache.Count;

    public void Invalidate(params EntityKind[] kinds)
    {
        if (kinds.Length == 0) _cache.Clear();
        foreach (var k in kinds) _cache.TryRemove(k, out _);
    }

    public Task<IReadOnlyList<(EntityRef Ref, string Name)>> NamesAsync(EntityKind kind, CancellationToken ct)
    {
        var now = _clock();
        if (_cache.TryGetValue(kind, out var hit) && now - hit.At < Ttl && !hit.Names.IsFaulted) return hit.Names;
        var task = loader(kind, null, CancellationToken.None);
        _cache[kind] = (now, task);
        return task;
    }

    public async Task<OracleResolution> ResolveAsync(string? text, IReadOnlyList<EntityKind> kinds, CancellationToken ct)
    {
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0) return new OracleResolution { Query = query };
        var candidates = new List<OracleCandidate>();
        foreach (var kind in kinds)
        {
            var names = Cached.Contains(kind) ? await NamesAsync(kind, ct).ConfigureAwait(false) : await loader(kind, query, ct).ConfigureAwait(false);
            foreach (var (r, name) in names)
            {
                var s = OracleFuzzy.Similarity(query, name);
                if (s >= OracleFuzzy.Plausible) candidates.Add(new OracleCandidate(r, name, s));
            }
        }
        candidates = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Name.Length).Take(8).ToList();
        return new OracleResolution { Query = query, Candidates = candidates, Single = PickSingle(candidates) };
    }

    /// <summary>One strong match, or an exact name that beats every other candidate.</summary>
    private static OracleCandidate? PickSingle(List<OracleCandidate> c)
    {
        if (c.Count == 0 || c[0].Score < OracleFuzzy.Strong) return null;
        if (c.Count == 1 || c[1].Score < OracleFuzzy.Strong) return c[0];
        if (c[0].Score >= 1.0 && c[1].Score < 1.0) return c[0];
        return null;
    }
}

public static class OracleIcons
{
    public static string Of(EntityKind kind) => kind switch
    {
        EntityKind.Game => "Games", EntityKind.Project => "Projects", EntityKind.Document => "Documents", EntityKind.Trip => "Suitcase",
        EntityKind.Place => "Pin", EntityKind.Task => "Check", EntityKind.Note => "Notes", EntityKind.Event => "Calendar",
        EntityKind.Diagnostic => "Ping", EntityKind.Journal => "Journal", EntityKind.Transaction => "Receipt", EntityKind.Account => "Bank",
        EntityKind.Budget => "Chart", EntityKind.Recurring => "Repeat", EntityKind.Goal => "Target",
        _ => "Sparkle",
    };

    public static EntityKind? KindOf(OracleEntity e) => e switch
    {
        OracleEntity.Game => EntityKind.Game, OracleEntity.Project => EntityKind.Project, OracleEntity.Task => EntityKind.Task,
        OracleEntity.Note => EntityKind.Note, OracleEntity.Document => EntityKind.Document, OracleEntity.Trip => EntityKind.Trip,
        OracleEntity.Place => EntityKind.Place, OracleEntity.Event => EntityKind.Event, OracleEntity.Account => EntityKind.Account,
        OracleEntity.Transaction => EntityKind.Transaction, OracleEntity.Budget => EntityKind.Budget, OracleEntity.Subscription => EntityKind.Recurring,
        OracleEntity.Bill => EntityKind.Recurring, OracleEntity.Goal => EntityKind.Goal, OracleEntity.Session => EntityKind.Diagnostic,
        OracleEntity.Journal => EntityKind.Journal,
        _ => null,
    };
}
