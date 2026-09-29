using Arcadia.Core.Organizer;
using Arcadia.Core.Search;

namespace Arcadia.Core.Oracle.Commands;

/// <summary>Shared scoring and result helpers for commands.</summary>
public abstract class OracleCommandBase : OracleCommand
{
    protected static bool IsQuery(OracleParse p) => p.Verb is null or "show" or "find" || p.Question is "howmany" or "what";
    protected static bool Is(OracleParse p, params OracleEntity[] e) => e.Contains(p.Entity);
    protected static bool Mentions(OracleParse p, OracleEntity e) => p.Entities.Contains(e);
    protected static bool Counting(OracleParse p) => p.Question == "howmany";

    /// <summary>The words that name something: the payload, else the subject, else the target.</summary>
    protected static string NameOf(OracleParse p) => (p.Payload ?? (p.Subject.Length > 0 ? p.Subject : p.Target ?? p.Topic ?? string.Empty)).Trim();

    protected static OracleAction OpenAction(OracleCall c, EntityRef r, string? tab = null, bool primary = true) =>
        new() { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, Tab = tab, IsPrimary = primary };

    protected static OracleAction NavigateAction(OracleCall c, string section, string? tab = null, string? label = null) =>
        new() { Label = label ?? c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Navigate, Section = section, Tab = tab };

    protected static OracleAction RunAction(string label, string command, string icon = "Play", bool primary = false) =>
        new() { Label = label, Icon = icon, Kind = OracleActionKind.Run, Command = command, IsPrimary = primary };

    protected static OracleResult ListResult(OracleCall c, string headline, List<OracleItem> items, string emptyKey, OracleAction? more = null, EntityRef? focus = null, string? focusName = null)
    {
        var result = new OracleResult
        {
            Kind = OracleResultKind.List,
            Headline = items.Count == 0 ? c.T(emptyKey) : headline,
            Items = items.Take(30).ToList(),
            Focus = focus,
            FocusName = focusName,
        };
        if (items.Count > 30) result.Detail = c.T("Orc_ShowingFirst", 30, items.Count);
        if (more != null) result.Actions.Add(more);
        return result;
    }

    protected static OracleResult Count(OracleCall c, string key, int n, OracleAction? open = null)
    {
        var r = new OracleResult { Kind = OracleResultKind.Metric, Headline = c.T(key, n) };
        if (open != null) r.Actions.Add(open);
        return r;
    }

    protected static bool InPeriod(OracleParse p, DateOnly? date) => p.Period is not { } per || (date is { } d && d >= per.From && d <= per.To);

    /// <summary>The record a list is scoped to: a named target ("for Home Server"), "its/this", or nothing.</summary>
    protected static async Task<(EntityRef Ref, string Name)?> ScopeAsync(OracleCall c, CancellationToken ct, params EntityKind[] kinds)
    {
        var p = c.P;
        if (p.Target is { Length: > 0 } target)
        {
            var only = OracleIcons.KindOf(p.TargetKind) is { } k && kinds.Contains(k) ? [k] : kinds;
            var r = await c.ResolveAsync(target, ct, only).ConfigureAwait(false);
            if (r.Single is { } s) return (s.Ref, s.Name);
            return null;
        }
        if (p.RefersBack && c.Referent(kinds) is { } referent) return (referent.Ref, referent.Name ?? string.Empty);
        return null;
    }

    /// <summary>A named target that did not resolve: "which one?" or "not found" instead of silently ignoring it.</summary>
    protected static async Task<OracleResult?> UnresolvedTargetAsync(OracleCall c, CancellationToken ct, Func<OracleCandidate, string> rerun, params EntityKind[] kinds)
    {
        if (c.P.Target is not { Length: > 0 } target) return null;
        var only = OracleIcons.KindOf(c.P.TargetKind) is { } k && kinds.Contains(k) ? [k] : kinds;
        var r = await c.ResolveAsync(target, ct, only).ConfigureAwait(false);
        if (r.Single != null) return null;
        return r.IsNone ? c.NotFound(target) : c.Choose(r, rerun);
    }
}

/// <summary>"open finance", "go to calendar", "open subscriptions", "open ping tool", "zeige Einstellungen".</summary>
public sealed class NavigateCommand : OracleCommandBase
{
    public override string Id => "nav.open";
    public override string Module => "home";
    public override OracleIntent Intent => OracleIntent.Navigate;
    public override string[] Examples => ["open finance", "go to calendar", "open subscriptions", "open IT tools"];
    public override string[] ExamplesDe => ["öffne Finanzen", "gehe zu Kalender"];
    public override string[] ExamplesAr => ["افتح المالية", "افتح التقويم"];

    private static readonly Dictionary<OracleEntity, (string Section, string? Tab)> EntitySections = new()
    {
        [OracleEntity.Game] = ("games", null), [OracleEntity.Project] = ("projects", null), [OracleEntity.Task] = ("tasks", null),
        [OracleEntity.Note] = ("notes", null), [OracleEntity.Document] = ("documents", null), [OracleEntity.Trip] = ("travel", "trips"),
        [OracleEntity.Place] = ("travel", "places"), [OracleEntity.Event] = ("calendar", null), [OracleEntity.Account] = ("finance", "accounts"),
        [OracleEntity.Transaction] = ("finance", "transactions"), [OracleEntity.Budget] = ("finance", "budgets"),
        [OracleEntity.Subscription] = ("finance", "recurring"), [OracleEntity.Bill] = ("finance", "recurring"), [OracleEntity.Goal] = ("finance", "goals"),
        [OracleEntity.Journal] = ("travel", "journal"), [OracleEntity.Bucket] = ("travel", "bucket"), [OracleEntity.Session] = ("ittools", "session"),
        [OracleEntity.Category] = ("finance", "categories"),
        [OracleEntity.Automation] = ("automation", null),
    };

    private static readonly Dictionary<string, string> ToolTabs = new()
    {
        ["ping"] = "ping", ["dns"] = "dns", ["trace"] = "trace", ["port"] = "port", ["sysinfo"] = "sysinfo", ["network"] = "network",
        ["storage"] = "storage", ["processes"] = "processes",
    };

    private static (string Section, string? Tab)? Target(OracleParse p)
    {
        if (p.Module != null) return (p.Module, p.Entity != OracleEntity.None && EntitySections.TryGetValue(p.Entity, out var s) && s.Section == p.Module ? s.Tab : null);
        if (p.Tool != null && p.Host == null && ToolTabs.TryGetValue(p.Tool, out var tab)) return ("ittools", tab);
        if (p.Entities.Count == 1 && EntitySections.TryGetValue(p.Entity, out var e)) return e;
        return null;
    }

    public override double Match(OracleParse p, OracleContext context)
    {
        if (Target(p) is null || p.Question != null || p.Metric != null || p.RefersBack || p.Payload != null) return 0;
        if (p.Subject.Length > 0 || p.Target != null || p.Topic != null) return 0; // "open Cyberpunk", "tasks for X" are not navigation
        var bare = p.Filters.Count == 0 && p.Period == null;
        if (p.Verb == "open") return bare ? 0.95 : 0.4;
        if (p.Verb is null or "show" && p.Module != null && bare) return 0.8;
        if (p.Verb is null && p.Tool != null && bare) return 0.5;
        return 0;
    }

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var (section, tab) = Target(c.P)!.Value;
        var name = c.T("Orc_Section_" + section);
        var action = NavigateAction(c, section, tab);
        return Task.FromResult(new OracleResult { Kind = OracleResultKind.Navigated, Headline = c.T("Orc_Opening", name), AutoAction = action });
    }
}

/// <summary>"help", "help finance", "what can you do": generated from the registry.</summary>
public sealed class HelpCommand : OracleCommandBase
{
    public override string Id => "help";
    public override string Module => "oracle";
    public override OracleIntent Intent => OracleIntent.Help;
    public override string[] Examples => ["help", "help finance", "what can you do"];
    public override string[] ExamplesDe => ["hilfe", "was kannst du"];
    public override string[] ExamplesAr => ["مساعدة", "ماذا يمكنك"];

    private static readonly Dictionary<OracleEntity, string> EntityModules = new()
    {
        [OracleEntity.Game] = "games", [OracleEntity.Project] = "projects", [OracleEntity.Task] = "tasks", [OracleEntity.Note] = "notes",
        [OracleEntity.Document] = "documents", [OracleEntity.Trip] = "travel", [OracleEntity.Place] = "travel", [OracleEntity.Event] = "calendar",
        [OracleEntity.Transaction] = "finance", [OracleEntity.Budget] = "finance", [OracleEntity.Subscription] = "finance",
    };

    public override double Match(OracleParse p, OracleContext context) => p.Verb == "help" ? 1.0 : 0;

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var module = p.Module ?? (p.Tool != null ? "ittools" : EntityModules.GetValueOrDefault(p.Entity));
        var groups = c.Engine.Commands.Where(x => x.Id != Id && (module == null || x.Module == module)).GroupBy(x => x.Module);
        var items = new List<OracleItem>();
        foreach (var g in groups)
            foreach (var cmd in g)
            {
                var example = cmd.ExamplesFor(p.Language)[0];
                items.Add(new OracleItem
                {
                    Title = example,
                    Subtitle = string.Join("  ·  ", cmd.ExamplesFor(p.Language).Skip(1).Take(2)),
                    Group = c.T("Orc_Section_" + g.Key),
                    Icon = cmd.Safety == OracleSafety.ReadOnly ? "Search" : "Plus",
                    Actions = [RunAction(c.T("Orc_Try"), example, primary: true)],
                });
            }
        return Task.FromResult(new OracleResult
        {
            Kind = OracleResultKind.Help,
            Headline = module == null ? c.T("Orc_HelpAll") : c.T("Orc_HelpModule", c.T("Orc_Section_" + module)),
            Detail = c.T("Orc_HelpDetail"),
            Items = items,
        });
    }
}

/// <summary>"reset", "start over": forgets the conversation (focus and last query).</summary>
public sealed class ResetCommand : OracleCommandBase
{
    public override string Id => "reset";
    public override string Module => "oracle";
    public override OracleIntent Intent => OracleIntent.Reset;
    public override string[] Examples => ["reset", "start over"];
    public override string[] ExamplesDe => ["zurücksetzen"];
    public override string[] ExamplesAr => ["امسح السياق"];
    public override double Match(OracleParse p, OracleContext context) => p.Verb == "reset" ? 1.0 : 0;

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        c.Context.Reset();
        return Task.FromResult(new OracleResult { Kind = OracleResultKind.Done, Headline = c.T("Orc_ContextCleared") });
    }
}

/// <summary>
/// "open Cyberpunk", "open project Home Server", "Cyberpunk screenshots", "open Italy trip documents": resolves a name across
/// Arcadia and opens it (on a tab when one is named). Several matches → choose; only weak matches → "did you mean".
/// </summary>
public sealed class OpenNamedCommand : OracleCommandBase
{
    public override string Id => "open.named";
    public override string Module => "home";
    public override OracleIntent Intent => OracleIntent.Open;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["open Cyberpunk", "open project Home Server", "Cyberpunk screenshots", "open Italy trip"];
    public override string[] ExamplesDe => ["öffne Cyberpunk", "öffne Projekt Home Server"];
    public override string[] ExamplesAr => ["افتح Cyberpunk", "افتح مشروع Home Server"];

    private static readonly EntityKind[] AllKinds =
        [EntityKind.Game, EntityKind.Project, EntityKind.Trip, EntityKind.Place, EntityKind.Account, EntityKind.Goal, EntityKind.Recurring, EntityKind.Document, EntityKind.Note, EntityKind.Task, EntityKind.Event, EntityKind.Journal];

    /// <summary>A tab of the record's page named by an entity word ("Cyberpunk journal", "Home Server tasks").</summary>
    public static string? TabFor(EntityKind kind, OracleEntity word) => (kind, word) switch
    {
        (EntityKind.Game, OracleEntity.Screenshot) => "Screenshots",
        (EntityKind.Game, OracleEntity.Journal or OracleEntity.Note) => "Journal",
        (EntityKind.Game, OracleEntity.Session) => "Sessions",
        (EntityKind.Project, OracleEntity.Task) => "Tasks",
        (EntityKind.Project, OracleEntity.Document) => "Documents",
        (EntityKind.Project, OracleEntity.Note) => "Notes",
        (EntityKind.Project, OracleEntity.Activity) => "Activity",
        (EntityKind.Project, OracleEntity.Transaction) => "Expenses",
        (EntityKind.Trip, OracleEntity.Document) => "Documents",
        (EntityKind.Trip, OracleEntity.Journal) => "Journal",
        (EntityKind.Trip, OracleEntity.Transaction or OracleEntity.Budget) => "Expenses",
        (EntityKind.Trip, OracleEntity.Place) => "Places",
        (EntityKind.Trip, OracleEntity.Screenshot) => "Photos",
        _ => null,
    };

    private static EntityKind[] KindsFor(OracleParse p)
    {
        // an entity word that names the kind ("project Home Server"), else every kind
        foreach (var e in p.Entities)
            if (e is OracleEntity.Game or OracleEntity.Project or OracleEntity.Trip or OracleEntity.Place or OracleEntity.Document or OracleEntity.Account or OracleEntity.Goal
                && OracleIcons.KindOf(e) is { } k)
                return [k];
        return AllKinds;
    }

    public override double Match(OracleParse p, OracleContext context)
    {
        var name = p.Payload ?? p.Subject;
        if (name.Length == 0 || p.Filters.Count > 0 || p.Question != null || p.Metric != null || p.Tool != null) return 0;
        if (p.Verb == "open") return 0.85;
        if (p.Verb is null or "show" && p.Entities.Count > 0 && p.Entities.Any(e => e is OracleEntity.Screenshot or OracleEntity.Journal or OracleEntity.Session or OracleEntity.Activity)) return 0.6;
        if (p.Verb is null or "show" && p.Entities.Count == 0) return 0.45; // a bare name: open it if it is one
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var name = (p.Payload ?? p.Subject).Trim();
        var kinds = KindsFor(p);
        var r = await c.ResolveAsync(name, ct, kinds).ConfigureAwait(false);
        if (r.Single is not { } hit)
        {
            if (r.IsNone) return p.Verb == "open" ? c.NotFound(name) : c.Engine.Unknown(c.Context, p.Language);
            var words = string.Join(' ', p.Entities.Where(e => e is not (OracleEntity.Game or OracleEntity.Project or OracleEntity.Trip or OracleEntity.Place)).Select(e => e.ToString().ToLowerInvariant()));
            return c.Choose(r, x => $"open {EntityRef.KindName(x.Ref.Kind)} {c.Quote(x.Name)} {words}".Trim());
        }
        var tab = p.Entities.Select(e => TabFor(hit.Ref.Kind, e)).FirstOrDefault(t => t != null);
        var action = OpenAction(c, hit.Ref, tab);
        return new OracleResult
        {
            Kind = OracleResultKind.Navigated,
            Headline = c.T("Orc_Opening", hit.Name),
            AutoAction = action,
            Focus = hit.Ref,
            FocusName = hit.Name,
            Confidence = hit.Score >= 0.95 ? OracleConfidence.High : OracleConfidence.Medium,
        };
    }
}

/// <summary>"find router", "search Italy": Arcadia's global search.</summary>
public sealed class SearchCommand : OracleCommandBase
{
    public override string Id => "search";
    public override string Module => "home";
    public override OracleIntent Intent => OracleIntent.Search;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["find router", "search Italy"];
    public override string[] ExamplesDe => ["suche Router"];
    public override string[] ExamplesAr => ["ابحث عن Italy"];

    public override double Match(OracleParse p, OracleContext context)
    {
        var text = SearchText(p);
        if (text.Length == 0) return 0;
        if (p.Verb == "find" && p.Entities.Count == 0) return 0.9;
        if (p.Verb == "find") return 0.55;
        return 0;
    }

    private static string SearchText(OracleParse p) => (p.Payload ?? string.Join(' ', new[] { p.Subject, p.Topic, p.Target }.Where(s => !string.IsNullOrEmpty(s)))).Trim();

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var text = SearchText(c.P);
        var hits = await c.S.Search.SearchAsync(new SearchRequest(text, null, 30), ct).ConfigureAwait(false);
        var items = hits.Select(h => Item(c, h)).ToList();
        return ListResult(c, c.T("Orc_SearchResults", items.Count, text), items, "Orc_SearchNone");
    }

    public static OracleItem Item(OracleCall c, SearchResult h)
    {
        var kind = h.Kind switch
        {
            SearchResultKind.Project => EntityKind.Project, SearchResultKind.Task => EntityKind.Task, SearchResultKind.Note => EntityKind.Note,
            SearchResultKind.Document => EntityKind.Document, SearchResultKind.Game => EntityKind.Game, SearchResultKind.Trip => EntityKind.Trip,
            SearchResultKind.Place => EntityKind.Place, SearchResultKind.Journal => EntityKind.Journal, SearchResultKind.Event => EntityKind.Event,
            SearchResultKind.Transaction => EntityKind.Transaction, SearchResultKind.Account => EntityKind.Account, SearchResultKind.Budget => EntityKind.Budget,
            SearchResultKind.Recurring => EntityKind.Recurring, SearchResultKind.Goal => EntityKind.Goal,
            _ => (EntityKind?)null,
        };
        var item = new OracleItem
        {
            Title = h.Title,
            Subtitle = h.Snippet,
            Icon = kind is { } k ? OracleIcons.Of(k) : "Compass",
            Group = c.T("Orc_Kind_" + (kind?.ToString() ?? h.Kind.ToString())),
            Ref = kind is { } k2 && h.Id > 0 ? new EntityRef(k2, h.Id) : null,
        };
        if (item.Ref is { } r) item.Actions.Add(OpenAction(c, r));
        else if (h.Kind == SearchResultKind.Link && h.ProjectId is { } pid) item.Actions.Add(OpenAction(c, new EntityRef(EntityKind.Project, pid), "Links"));
        return item;
    }
}

/// <summary>"everything related to Home Server", "alles zu Italien": the record's tasks, notes, documents, events and expenses.</summary>
public sealed class RelatedCommand : OracleCommandBase
{
    public override string Id => "related";
    public override string Module => "home";
    public override OracleIntent Intent => OracleIntent.Show;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["everything related to Home Server", "show everything for Italy trip"];
    public override string[] ExamplesDe => ["alles zu Home Server"];
    public override string[] ExamplesAr => ["كل شيء عن Home Server"];

    private static readonly EntityKind[] Kinds = [EntityKind.Project, EntityKind.Trip, EntityKind.Game, EntityKind.Place];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (!p.AllRelated) return 0;
        return NameOf(p).Length > 0 || p.RefersBack || context.Referent != null ? 0.9 : 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        (EntityRef Ref, string Name)? scope = null;
        var name = NameOf(p);
        if (name.Length > 0)
        {
            var kinds = OracleIcons.KindOf(p.TargetKind) is { } k && Kinds.Contains(k) ? [k] : Kinds;
            var r = await c.ResolveAsync(name, ct, kinds).ConfigureAwait(false);
            if (r.Single is { } s) scope = (s.Ref, s.Name);
            else return r.IsNone ? c.NotFound(name) : c.Choose(r, x => $"everything related to {EntityRef.KindName(x.Ref.Kind)} {c.Quote(x.Name)}");
        }
        else if (c.Context.Referent is { } referent) scope = (referent.Ref, referent.Name ?? string.Empty);
        var (target, title) = scope!.Value;

        var items = new List<OracleItem>();
        var tasks = await c.S.Tasks.QueryAsync(new TaskQuery(TaskView.All, Context: target, Take: 40), c.Now, ct).ConfigureAwait(false);
        items.AddRange(tasks.Where(t => t.Status is not (TaskItemStatus.Completed or TaskItemStatus.Cancelled)).Select(t => TaskCommands.Item(c, t, c.T("Orc_Group_Tasks"))));
        var notes = await c.S.Notes.QueryAsync(new NoteQuery(Take: 300), ct).ConfigureAwait(false);
        items.AddRange(notes.Where(n => n.Context == target).Take(15).Select(n => NoteCommands.Item(c, n, c.T("Orc_Group_Notes"))));
        if (target.Kind == EntityKind.Project)
        {
            var docs = await c.S.Documents.ListForProjectAsync(target.Id, ct).ConfigureAwait(false);
            items.AddRange(docs.Take(15).Select(d => DocumentCommands.Item(c, d, c.T("Orc_Group_Documents"))));
            var exp = await c.S.Finance.GetProjectExpensesAsync(target.Id, c.Env.UiLanguage, ct).ConfigureAwait(false);
            items.AddRange(exp.Transactions.Take(10).Select(t => FinanceCommands.Item(c, t, c.T("Orc_Group_Expenses"))));
        }
        else if (target.Kind == EntityKind.Trip)
        {
            var docs = await c.S.Travel.ListTripDocumentsAsync(target.Id, ct).ConfigureAwait(false);
            items.AddRange(docs.Take(15).Select(d => DocumentCommands.Item(c, d.Document, c.T("Orc_Group_Documents"))));
            var journal = await c.S.Travel.ListJournalAsync(new Travel.JournalQuery(target.Id), ct).ConfigureAwait(false);
            items.AddRange(journal.Take(10).Select(j => new OracleItem
            {
                Title = j.Title, Subtitle = c.Env.Date(j.EntryDate), Icon = "Journal", Group = c.T("Orc_Group_Journal"),
                Ref = new EntityRef(EntityKind.Journal, j.Id), Actions = [OpenAction(c, new EntityRef(EntityKind.Journal, j.Id))],
            }));
            var exp = await c.S.Finance.GetTripExpensesAsync(target.Id, c.Env.UiLanguage, ct).ConfigureAwait(false);
            items.AddRange(exp.Transactions.Take(10).Select(t => FinanceCommands.Item(c, t, c.T("Orc_Group_Expenses"))));
        }
        var events = await c.S.Agenda.GetItemsAsync(c.Now.Date.AddDays(-1), c.Now.Date.AddDays(90), CalendarSources.Events, ct).ConfigureAwait(false);
        items.AddRange(events.Where(e => e.Context == target).Take(10).Select(e => CalendarCommands.Item(c, e, c.T("Orc_Group_Events"))));

        var result = ListResult(c, c.T("Orc_RelatedTo", title), items, "Orc_RelatedNone", focus: target, focusName: title);
        result.Actions.Add(new OracleAction { Label = c.T("Orc_OpenName", title), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = target, IsPrimary = true });
        return result;
    }
}
