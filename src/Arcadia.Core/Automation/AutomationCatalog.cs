using System.Globalization;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Automation;

/// <summary>
/// The built-in triggers, conditions and actions. Event data keys: exe/path/pid (process), game/gameTitle/start/end/minutes
/// (game), path/file/ext/folder (file), task/taskTitle/project/projectName, from/to (project status), trip/tripName,
/// budget/budgetName/percent, bill/billName/due, goal/goalName, event/eventTitle/start, and always time/date.
/// </summary>
public static class AutomationCatalog
{
    public static readonly string[] Sections = ["home", "games", "ittools", "projects", "documents", "travel", "oracle", "calendar", "tasks", "notes", "finance", "automation", "settings"];
    public static readonly string[] ProjectStatuses = ["Planning", "Active", "OnHold", "Completed", "Archived"];
    public static readonly string[] CaptureKinds = ["Task", "Note", "Event", "Expense"];
    public static readonly string[] ItTools = ["sysinfo", "performance", "network", "ping", "trace", "dns", "port", "storage", "processes", "services", "eventlog"];

    private static FieldSpec Opt(string key, FieldKind kind, string? def = null, params string[] choices) => new(key, kind, false, choices.Length > 0 ? choices : null, def);
    private static FieldSpec Req(string key, FieldKind kind, params string[] choices) => new(key, kind, true, choices.Length > 0 ? choices : null);

    private static bool AnyOr(AutomationStep s, string key, string? value) => s.Get(key) is not { } want || want == "any" || string.Equals(want, value, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> D(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    private static string Ns(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static AutomationRegistry CreateDefault()
    {
        var r = new AutomationRegistry();

        // ── triggers ─────────────────────────────────────────
        r.Add(new TriggerDescriptor("process.started", "system", "Processes", Req("exe", FieldKind.Executable))
        {
            Matches = (s, e) => AutomationText.ExeName(s.Get("exe") ?? "") == e.Get("exe"),
            Sample = (s, now) => new("process.started", D(("exe", AutomationText.ExeName(s.Get("exe") ?? "app.exe")), ("pid", "0")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("process.stopped", "system", "Processes", Req("exe", FieldKind.Executable))
        {
            Matches = (s, e) => AutomationText.ExeName(s.Get("exe") ?? "") == e.Get("exe"),
            Sample = (s, now) => new("process.stopped", D(("exe", AutomationText.ExeName(s.Get("exe") ?? "app.exe"))), Simulated: true),
        });
        r.Add(new TriggerDescriptor("game.started", "games", "Games", Opt("game", FieldKind.Game, "any"))
        {
            Matches = (s, e) => AnyOr(s, "game", e.Get("game")),
            Sample = (s, now) => new("game.started", D(("game", s.Get("game") is { } g && g != "any" ? g : "0"), ("gameTitle", "Sample game"), ("start", Ns(now))), Simulated: true),
        });
        r.Add(new TriggerDescriptor("game.stopped", "games", "Games", Opt("game", FieldKind.Game, "any"))
        {
            Matches = (s, e) => AnyOr(s, "game", e.Get("game")),
            Sample = (s, now) => new("game.stopped", D(("game", s.Get("game") is { } g && g != "any" ? g : "0"), ("gameTitle", "Sample game"),
                ("start", Ns(now.AddMinutes(-42))), ("end", Ns(now)), ("minutes", "42")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("schedule", "time", "Clock",
            Req("repeat", FieldKind.Choice, "daily", "weekly", "monthly", "once"), Req("time", FieldKind.Time),
            Opt("days", FieldKind.Days) with { ShowWhen = "repeat=weekly" }, Opt("day", FieldKind.Number) with { ShowWhen = "repeat=monthly" },
            Opt("date", FieldKind.Date) with { ShowWhen = "repeat=once" })
        {
            Sample = (s, now) => new("schedule", D(("time", s.Get("time") ?? "")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("app.started", "arcadia", "Home"));
        r.Add(new TriggerDescriptor("app.closing", "arcadia", "WindowClose"));
        r.Add(new TriggerDescriptor("file.created", "files", "Import", Req("folder", FieldKind.Folder), Opt("extensions", FieldKind.Extensions), Opt("subfolders", FieldKind.Bool, "false"))
        {
            Matches = (s, e) => FileMatches(s, e),
            Sample = (s, now) => SampleFile("file.created", s),
        });
        r.Add(new TriggerDescriptor("file.changed", "files", "Documents", Req("folder", FieldKind.Folder), Opt("extensions", FieldKind.Extensions), Opt("subfolders", FieldKind.Bool, "false"))
        {
            Matches = (s, e) => FileMatches(s, e),
            Sample = (s, now) => SampleFile("file.changed", s),
        });
        r.Add(new TriggerDescriptor("task.completed", "organizer", "Tasks", Opt("project", FieldKind.Project, "any"))
        {
            Matches = (s, e) => AnyOr(s, "project", e.Get("project")),
            Sample = (s, now) => new("task.completed", D(("task", "0"), ("taskTitle", "Sample task")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("project.status", "projects", "Projects", Opt("project", FieldKind.Project, "any"), Opt("to", FieldKind.Choice, null, ProjectStatuses))
        {
            Matches = (s, e) => AnyOr(s, "project", e.Get("project")) && AnyOr(s, "to", e.Get("to")),
            Sample = (s, now) => new("project.status", D(("project", s.Get("project") is { } p && p != "any" ? p : "0"), ("projectName", "Sample project"),
                ("from", "Active"), ("to", s.Get("to") ?? "Completed")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("trip.started", "travel", "Suitcase", Opt("trip", FieldKind.Trip, "any"))
        {
            Matches = (s, e) => AnyOr(s, "trip", e.Get("trip")),
            Sample = (s, now) => new("trip.started", D(("trip", "0"), ("tripName", "Sample trip")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("trip.ended", "travel", "Suitcase", Opt("trip", FieldKind.Trip, "any"))
        {
            Matches = (s, e) => AnyOr(s, "trip", e.Get("trip")),
            Sample = (s, now) => new("trip.ended", D(("trip", "0"), ("tripName", "Sample trip")), Simulated: true),
        });
        r.Add(new TriggerDescriptor("event.upcoming", "calendar", "Calendar", Opt("minutes", FieldKind.Number, "10"))
        {
            Matches = (s, e) => (s.GetInt("minutes") ?? 10) == e.GetInt("lead"),
            Sample = (s, now) => new("event.upcoming", D(("event", "0"), ("eventTitle", "Sample event"), ("start", Ns(now.AddMinutes(10)))), Simulated: true),
        });
        r.Add(new TriggerDescriptor("budget.threshold", "finance", "Chart", Opt("percent", FieldKind.Number, "90"))
        {
            Matches = (s, e) => (e.GetInt("percent") ?? 0) >= (s.GetInt("percent") ?? 90),
            Sample = (s, now) => new("budget.threshold", D(("budget", "0"), ("budgetName", "Sample budget"), ("percent", (s.GetInt("percent") ?? 90).ToString(CultureInfo.InvariantCulture))), Simulated: true),
        });
        r.Add(new TriggerDescriptor("bill.due", "finance", "Receipt", Opt("days", FieldKind.Number, "1"))
        {
            Matches = (s, e) => (s.GetInt("days") ?? 1) == e.GetInt("days"),
            Sample = (s, now) => new("bill.due", D(("bill", "0"), ("billName", "Sample bill"), ("due", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), Simulated: true),
        });
        r.Add(new TriggerDescriptor("goal.completed", "finance", "Target")
        {
            Sample = (s, now) => new("goal.completed", D(("goal", "0"), ("goalName", "Sample goal")), Simulated: true),
        });

        // ── conditions ───────────────────────────────────────
        r.Add(new ConditionDescriptor("time.between", "time", "Clock", Req("after", FieldKind.Time), Req("before", FieldKind.Time))
        {
            Evaluate = (s, e, h) => AutomationText.InWindow(TimeOnly.FromDateTime(h.Now), TimeOnly.ParseExact(s.Get("after")!, "HH:mm", CultureInfo.InvariantCulture), TimeOnly.ParseExact(s.Get("before")!, "HH:mm", CultureInfo.InvariantCulture)),
        });
        r.Add(new ConditionDescriptor("weekday", "time", "Calendar", Req("days", FieldKind.Days))
        {
            Evaluate = (s, e, h) => AutomationText.ParseDays(s.Get("days")).HasFlag(Recurrence.MaskOf(h.Now.DayOfWeek)),
        });
        r.Add(new ConditionDescriptor("gaming", "arcadia", "Games", Req("state", FieldKind.Choice, "on", "off"))
        {
            Evaluate = (s, e, h) => h.IsGamingMode == (s.Get("state") == "on"),
        });
        r.Add(new ConditionDescriptor("process.running", "system", "Processes", Req("exe", FieldKind.Executable), Opt("state", FieldKind.Choice, "running", "running", "notrunning"))
        {
            Evaluate = (s, e, h) => h.IsProcessRunning(AutomationText.ExeName(s.Get("exe")!)) == (s.Get("state") != "notrunning"),
        });
        r.Add(new ConditionDescriptor("file.extension", "files", "Documents", Req("extensions", FieldKind.Extensions))
        {
            Evaluate = (s, e, h) => e.Get("ext") is { } ext && AutomationText.Extensions(s.Get("extensions")).Contains(ext),
        });
        r.Add(new ConditionDescriptor("game.is", "games", "Games", Req("game", FieldKind.Game))
        {
            Evaluate = (s, e, h) => s.Get("game") == e.Get("game"),
        });
        r.Add(new ConditionDescriptor("project.is", "projects", "Projects", Req("project", FieldKind.Project))
        {
            Evaluate = (s, e, h) => s.Get("project") == e.Get("project"),
        });
        r.Add(new ConditionDescriptor("power", "system", "Bolt", Req("source", FieldKind.Choice, "ac", "battery"))
        {
            Evaluate = (s, e, h) => (h.IsOnAcPower ?? true) == (s.Get("source") == "ac"),
        });
        r.Add(new ConditionDescriptor("network", "system", "Network", Req("state", FieldKind.Choice, "online", "offline"))
        {
            Evaluate = (s, e, h) => h.IsNetworkAvailable == (s.Get("state") == "online"),
        });

        // ── actions (none can delete, kill, edit the registry, change services or run commands) ──
        r.Add(new ActionDescriptor("notify", "arcadia", "Bell", ActionSafety.Safe, Req("title", FieldKind.Text), Opt("text", FieldKind.Text))
        {
            Execute = async (s, e, h, ct) =>
            {
                await h.NotifyAsync("automation", AutomationText.Fill(s.Get("title")!, e), AutomationText.Fill(s.Get("text") ?? "", e), e.Subject);
                return null;
            },
        });
        r.Add(new ActionDescriptor("open.section", "arcadia", "ArrowRight", ActionSafety.Safe, Req("section", FieldKind.Section, Sections), Opt("tab", FieldKind.Text))
        {
            Execute = async (s, e, h, ct) => { await h.OpenSectionAsync(s.Get("section")!, s.Get("tab")); return null; },
        });
        r.Add(new ActionDescriptor("open.today", "organizer", "Agenda", ActionSafety.Safe)
        {
            Execute = async (s, e, h, ct) => { await h.OpenSectionAsync("calendar", "Today"); return null; },
        });
        r.Add(new ActionDescriptor("open.entity", "arcadia", "Link", ActionSafety.Safe, Opt("uri", FieldKind.Uri))
        {
            Execute = async (s, e, h, ct) =>
            {
                var target = s.Get("uri") is { } u && EntityRef.TryParse(u, out var parsed) ? parsed : e.Subject;
                if (target is not { } t) return h.T("Auto_NoRecord");
                await h.OpenEntityAsync(t);
                return t.ToString();
            },
        });
        r.Add(new ActionDescriptor("quickcapture", "organizer", "Bolt", ActionSafety.Safe, Req("kind", FieldKind.Choice, CaptureKinds), Opt("text", FieldKind.Text))
        {
            Execute = async (s, e, h, ct) => { await h.OpenQuickCaptureAsync(s.Get("kind")!, s.Get("text") is { } t ? AutomationText.Fill(t, e) : null); return null; },
        });
        r.Add(new ActionDescriptor("task.create", "organizer", "Tasks", ActionSafety.Safe, Req("title", FieldKind.Text), Opt("dueDays", FieldKind.Number), Opt("link", FieldKind.Bool, "true"))
        {
            AllowedOnExit = true,
            Execute = async (s, e, h, ct) =>
            {
                DateOnly? due = s.GetInt("dueDays") is { } d ? DateOnly.FromDateTime(h.Now).AddDays(Math.Clamp(d, 0, 3650)) : null;
                var id = await h.CreateTaskAsync(AutomationText.Fill(s.Get("title")!, e), due, s.Get("link") == "false" ? null : e.Subject, ct);
                return "arcadia://task/" + id;
            },
        });
        r.Add(new ActionDescriptor("note.create", "organizer", "Notes", ActionSafety.Safe, Req("title", FieldKind.Text), Opt("text", FieldKind.Text), Opt("link", FieldKind.Bool, "true"))
        {
            AllowedOnExit = true,
            Execute = async (s, e, h, ct) =>
            {
                var id = await h.CreateNoteAsync(AutomationText.Fill(s.Get("title")!, e), AutomationText.Fill(s.Get("text") ?? "", e), s.Get("link") == "false" ? null : e.Subject, ct);
                return "arcadia://note/" + id;
            },
        });
        r.Add(new ActionDescriptor("gaming.on", "games", "Games", ActionSafety.Safe)
        {
            Execute = (s, e, h, ct) => { h.SetGamingMode(true, "automation"); return Task.FromResult<string?>(null); },
        });
        r.Add(new ActionDescriptor("gaming.off", "games", "Games", ActionSafety.Safe)
        {
            AllowedOnExit = true,
            Execute = (s, e, h, ct) => { h.SetGamingMode(false, "automation"); return Task.FromResult<string?>(null); },
        });
        r.Add(new ActionDescriptor("game.playing", "games", "Play", ActionSafety.Safe, Opt("game", FieldKind.Game))
        {
            Execute = async (s, e, h, ct) =>
            {
                var id = s.GetInt("game") ?? e.GetInt("game");
                if (id is not > 0) return h.T("Auto_NoRecord");
                await h.SetGameStatusPlayingAsync(id.Value, ct);
                return "arcadia://game/" + id;
            },
        });
        r.Add(new ActionDescriptor("game.session.start", "games", "Play", ActionSafety.Safe, Opt("game", FieldKind.Game))
        {
            Execute = async (s, e, h, ct) =>
            {
                var id = s.GetInt("game") ?? e.GetInt("game");
                if (id is not > 0) return h.T("Auto_NoRecord");
                await h.ExpectGameAsync(id.Value, ct);
                return "arcadia://game/" + id;
            },
        });
        r.Add(new ActionDescriptor("game.session.stop", "games", "Stop", ActionSafety.Safe)
        {
            AllowedOnExit = true,
            Execute = async (s, e, h, ct) => { await h.StopGameSessionAsync(ct); return null; },
        });
        r.Add(new ActionDescriptor("work.start", "projects", "Clock", ActionSafety.Safe, Opt("project", FieldKind.Project))
        {
            Execute = async (s, e, h, ct) =>
            {
                var id = s.GetInt("project") ?? e.GetInt("project");
                if (id is not > 0) return h.T("Auto_NoRecord");
                await h.StartWorkSessionAsync(id.Value, automatic: true, ct);
                return "arcadia://project/" + id;
            },
        });
        r.Add(new ActionDescriptor("work.stop", "projects", "Stop", ActionSafety.Safe)
        {
            AllowedOnExit = true,
            Execute = async (s, e, h, ct) => { await h.StopWorkSessionAsync(ct); return null; },
        });
        r.Add(new ActionDescriptor("ping", "ittools", "Ping", ActionSafety.Safe, Req("host", FieldKind.Text))
        {
            Execute = async (s, e, h, ct) =>
            {
                var result = await h.PingAsync(s.Get("host")!, ct);
                await h.NotifyAsync("automation", "Ping " + s.Get("host"), result);
                return result;
            },
        });
        r.Add(new ActionDescriptor("it.open", "ittools", "ITTools", ActionSafety.Safe, Req("tool", FieldKind.Choice, ItTools))
        {
            Execute = async (s, e, h, ct) => { await h.OpenSectionAsync("ittools", s.Get("tool")); return null; },
        });
        r.Add(new ActionDescriptor("file.open", "files", "Documents", ActionSafety.Safe, Opt("path", FieldKind.Text))
        {
            Execute = async (s, e, h, ct) =>
            {
                var path = s.Get("path") is { } p ? AutomationText.Fill(p, e) : e.Get("path");
                if (path == null) return h.T("Auto_NoFile");
                await h.OpenPathAsync(path, revealFolder: false);
                return path;
            },
        });
        r.Add(new ActionDescriptor("folder.open", "files", "Projects", ActionSafety.Safe, Opt("path", FieldKind.Text))
        {
            Execute = async (s, e, h, ct) =>
            {
                var path = s.Get("path") is { } p ? AutomationText.Fill(p, e) : e.Get("path");
                if (path == null) return h.T("Auto_NoFile");
                await h.OpenPathAsync(path, revealFolder: true);
                return path;
            },
        });
        r.Add(new ActionDescriptor("document.add", "files", "Import", ActionSafety.Safe, Opt("project", FieldKind.Project))
        {
            Execute = async (s, e, h, ct) => e.Get("path") is { } path ? await h.AddDocumentAsync(path, s.GetInt("project"), ct) : h.T("Auto_NoFile"),
        });
        r.Add(new ActionDescriptor("project.status", "projects", "Projects", ActionSafety.Confirm, Opt("project", FieldKind.Project), Req("status", FieldKind.Choice, ProjectStatuses))
        {
            Execute = async (s, e, h, ct) =>
            {
                var id = s.GetInt("project") ?? e.GetInt("project");
                if (id is not > 0) return h.T("Auto_NoRecord");
                await h.SetProjectStatusAsync(id.Value, s.Get("status")!, ct);
                return "arcadia://project/" + id + " → " + s.Get("status");
            },
        });
        return r;
    }

    private static bool FileMatches(AutomationStep s, AutomationEvent e)
    {
        if (s.Get("folder") is not { } folder || e.Get("path") is not { } path) return false;
        var dir = Path.GetDirectoryName(path) ?? "";
        var root = Path.TrimEndingDirectorySeparator(folder);
        var inside = s.GetBool("subfolders")
            ? dir.Equals(root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : dir.Equals(root, StringComparison.OrdinalIgnoreCase);
        var exts = AutomationText.Extensions(s.Get("extensions"));
        return inside && (exts.Count == 0 || exts.Contains(e.Get("ext") ?? ""));
    }

    private static AutomationEvent SampleFile(string type, AutomationStep s)
    {
        var ext = AutomationText.Extensions(s.Get("extensions")).FirstOrDefault() ?? "pdf";
        var path = Path.Combine(s.Get("folder") ?? Path.GetTempPath(), "sample." + ext);
        return new(type, D(("path", path), ("file", Path.GetFileName(path)), ("ext", ext), ("folder", Path.GetDirectoryName(path) ?? "")), Simulated: true);
    }
}
