using System.Globalization;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;

namespace Arcadia.Core.Oracle.Commands;

// ─────────────────────────── projects ───────────────────────────

/// <summary>"show active projects", "projects on hold", "projects with deadlines this month", "how many projects are active".</summary>
public sealed class ProjectListCommand : OracleCommandBase
{
    public override string Id => "projects.list";
    public override string Module => "projects";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Project];
    public override bool CanRefine => true;
    public override string[] Examples => ["show active projects", "projects on hold", "projects due this month", "pinned projects"];
    public override string[] ExamplesDe => ["zeige aktive Projekte"];
    public override string[] ExamplesAr => ["اعرض المشاريع النشطة"];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Project) && p.Entities.Count == 1 && IsQuery(p) && p.Target == null && !p.AllRelated ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        IEnumerable<ProjectSummary> list = await c.S.Projects.ListAsync(ct).ConfigureAwait(false);
        var today = c.Today;
        if (p.HasFilter("completed")) list = list.Where(x => x.Status == ProjectStatus.Completed);
        else if (!p.HasFilter("planning") && !p.HasFilter("onhold")) list = list.Where(x => x.Status != ProjectStatus.Archived && (x.Status != ProjectStatus.Completed || p.HasFilter("recent")));
        if (p.HasFilter("active") || p.HasFilter("unfinished") || p.HasFilter("playing")) list = list.Where(x => x.Status == ProjectStatus.Active);
        if (p.HasFilter("onhold")) list = list.Where(x => x.Status == ProjectStatus.OnHold);
        if (p.HasFilter("planning")) list = list.Where(x => x.Status == ProjectStatus.Planning);
        if (p.HasFilter("pinned")) list = list.Where(x => x.IsPinned);
        if (p.HasFilter("high")) list = list.Where(x => x.Priority >= ProjectPriority.High);
        if (p.HasFilter("overdue")) list = list.Where(x => x.TargetDate < today && x.Status != ProjectStatus.Completed);
        if (p.HasFilter("upcoming") || p.HasFilter("due") || p.Period != null)
        {
            var (from, to) = p.Period is { } per ? (per.From, per.To) : (today, today.AddDays(30));
            list = list.Where(x => x.TargetDate is { } d && d >= from && d <= to).OrderBy(x => x.TargetDate);
        }
        if (p.Topic is { Length: > 0 } topic) list = list.Where(x => OracleFuzzy.Similarity(topic, x.Tags) >= OracleFuzzy.Strong || OracleFuzzy.Similarity(topic, x.Name) >= OracleFuzzy.Strong);
        var rows = list.ToList();
        if (Counting(p)) return Count(c, "Orc_CountProjects", rows.Count, NavigateAction(c, "projects"));
        var items = rows.Select(x => Item(c, x)).ToList();
        return ListResult(c, c.T("Orc_ListProjects", items.Count), items, "Orc_NoProjects", NavigateAction(c, "projects", label: c.T("Orc_OpenProjects")));
    }

    public static OracleItem Item(OracleCall c, ProjectSummary x)
    {
        var r = new EntityRef(EntityKind.Project, x.Id);
        var sub = c.T("Orc_ProjectStatus_" + x.Status) + " · " + c.T("Orc_OpenTasks", x.OpenTasks);
        if (x.TargetDate is { } d) sub += " · " + c.T("Orc_Due", c.Env.Date(d));
        return new OracleItem
        {
            Title = x.Name, Subtitle = sub, Value = x.Progress + " %", Progress = x.Progress / 100.0, Icon = "Projects", Accent = x.Accent,
            Kind = OracleEntity.Project, Ref = r,
            Actions = [OpenAction(c, r), OpenAction(c, r, "Tasks", false)],
        };
    }
}

/// <summary>"create project Home Server", "neues Projekt Garten".</summary>
public sealed class ProjectCreateCommand : OracleCommandBase
{
    public override string Id => "projects.create";
    public override string Module => "projects";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Project];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["create project Garden Shed", "new project \"Home Lab v2\""];
    public override string[] ExamplesDe => ["neues Projekt Garten"];
    public override string[] ExamplesAr => ["أنشئ مشروع Garden"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Verb == "create" && Is(p, OracleEntity.Project) && (p.Payload ?? p.Subject).Length > 0 ? 0.9 : 0;

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var name = (c.P.Payload ?? c.P.Subject).Trim();
        var due = c.P.Period?.To;
        var preview = new OraclePreview
        {
            Title = c.T("Orc_CreateProjectTitle"),
            Icon = "Projects",
            Fields = [(c.T("Orc_Field_Name"), name), (c.T("Orc_Field_Status"), c.T("Orc_ProjectStatus_Active"))],
            Confirm = c.ConfirmAction(async t =>
            {
                var project = await c.S.Projects.CreateAsync(new ProjectDraft(name, null, ProjectStatus.Active, ProjectPriority.Normal, c.Today, due, "Blue", "Projects", "mountains-dusk", "", null), t).ConfigureAwait(false);
                return GameUpdateCommand.Done(c, c.T("Orc_Created", name), new EntityRef(EntityKind.Project, project.Id), name);
            }),
        };
        if (due is { } d) preview.Fields.Add((c.T("Orc_Field_Target"), c.Env.Date(d)));
        return c.ChangeAsync(preview, true, ct);
    }
}

/// <summary>"set Home Server to on hold", "mark project X as completed", "pin project X", "unpin it".</summary>
public sealed class ProjectUpdateCommand : OracleCommandBase
{
    public override string Id => "projects.update";
    public override string Module => "projects";
    public override OracleIntent Intent => OracleIntent.Update;
    public override OracleEntity[] Entities => [OracleEntity.Project];
    public override OracleSafety Safety => OracleSafety.Update;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["set project Home Server to on hold", "mark project Garden as completed", "pin project Home Server"];
    public override string[] ExamplesDe => ["setze Projekt Home Server auf pausiert"];
    public override string[] ExamplesAr => ["ثبت مشروع Home Server"];

    private static ProjectStatus? StatusOf(OracleParse p) =>
        p.HasFilter("completed") || p.Verb == "complete" ? ProjectStatus.Completed
        : p.HasFilter("onhold") ? ProjectStatus.OnHold
        : p.HasFilter("active") || p.HasFilter("playing") ? ProjectStatus.Active
        : p.HasFilter("planning") ? ProjectStatus.Planning : null;

    public override double Match(OracleParse p, OracleContext context)
    {
        var projectWord = Mentions(p, OracleEntity.Project) || context.Module == "projects" || (p.RefersBack && context.Referent is { Ref.Kind: EntityKind.Project });
        if (!projectWord || p.Entity is not (OracleEntity.None or OracleEntity.Project)) return 0;
        if (NameOf(p).Length == 0 && !(p.RefersBack && context.Referent is { Ref.Kind: EntityKind.Project })) return 0;
        if (p.Verb is "pin" or "unpin") return 0.85;
        if (p.Verb is "set" or "complete" && StatusOf(p) != null) return 0.85;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        EntityRef target;
        string name;
        var exact = true;
        if (NameOf(p) is { Length: > 0 } text)
        {
            var r = await c.ResolveAsync(text, ct, EntityKind.Project).ConfigureAwait(false);
            if (r.Single is not { } s)
                return r.IsNone ? c.NotFound(text) : c.Choose(r, x => p.Verb is "pin" or "unpin" ? $"{p.Verb} project \"{x.Name}\"" : $"set project \"{x.Name}\" to {StatusWord(StatusOf(p))}");
            (target, name, exact) = (s.Ref, s.Name, r.IsExact);
        }
        else
        {
            var referent = c.Referent(EntityKind.Project)!.Value;
            (target, name) = (referent.Ref, referent.Name ?? string.Empty);
        }

        if (p.Verb is "pin" or "unpin")
        {
            var pin = p.Verb == "pin";
            return await c.ChangeAsync(new OraclePreview
            {
                Title = c.T(pin ? "Orc_PinTitle" : "Orc_UnpinTitle", name),
                Icon = "Pin",
                Confirm = c.ConfirmAction(async t =>
                {
                    await c.S.Projects.SetPinnedAsync(target.Id, pin, t).ConfigureAwait(false);
                    return GameUpdateCommand.Done(c, c.T(pin ? "Orc_Pinned" : "Orc_Unpinned", name), target, name);
                }),
            }, exact, ct).ConfigureAwait(false);
        }
        var status = StatusOf(p)!.Value;
        var label = c.T("Orc_ProjectStatus_" + status);
        return await c.ChangeAsync(new OraclePreview
        {
            Title = c.T("Orc_SetStatusTitle", name, label),
            Icon = "Projects",
            Fields = [(c.T("Orc_Field_Status"), label)],
            Confirm = c.ConfirmAction(async t =>
            {
                await c.S.Projects.SetStatusAsync(target.Id, status, t).ConfigureAwait(false);
                return GameUpdateCommand.Done(c, c.T("Orc_StatusSet", name, label), target, name);
            }),
        }, exact, ct).ConfigureAwait(false);
    }

    private static string StatusWord(ProjectStatus? s) => s switch { ProjectStatus.OnHold => "on hold", ProjectStatus.Completed => "completed", ProjectStatus.Planning => "planning", _ => "active" };
}

/// <summary>"show activity for Home Server", "what happened in this project".</summary>
public sealed class ProjectActivityCommand : OracleCommandBase
{
    public override string Id => "projects.activity";
    public override string Module => "projects";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Activity];
    public override bool UsesFreeText => true;
    public override string[] Examples => ["show activity for Home Server", "recent activity of this project"];
    public override string[] ExamplesDe => ["Verlauf von Home Server"];

    public override double Match(OracleParse p, OracleContext context) =>
        Mentions(p, OracleEntity.Activity) && IsQuery(p) && (NameOf(p).Length > 0 || context.Referent is { Ref.Kind: EntityKind.Project }) ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        EntityRef target;
        string name;
        if (NameOf(c.P) is { Length: > 0 } text)
        {
            var r = await c.ResolveAsync(text, ct, EntityKind.Project).ConfigureAwait(false);
            if (r.Single is not { } s) return r.IsNone ? c.NotFound(text) : c.Choose(r, x => $"show activity for project \"{x.Name}\"");
            (target, name) = (s.Ref, s.Name);
        }
        else
        {
            var referent = c.Referent(EntityKind.Project)!.Value;
            (target, name) = (referent.Ref, referent.Name ?? string.Empty);
        }
        var rows = await c.S.Projects.GetActivityAsync(target.Id, 20, ct).ConfigureAwait(false);
        var items = rows.Select(a => new OracleItem
        {
            Title = a.Subject ?? c.T("Orc_Activity_" + a.Kind),
            Subtitle = c.T("Orc_Activity_" + a.Kind),
            Value = c.Env.Date(DateOnly.FromDateTime(a.CreatedAtUtc.ToLocalTime())),
            Icon = "Ping",
        }).ToList();
        return ListResult(c, c.T("Orc_ActivityOf", name), items, "Orc_NoActivity", OpenAction(c, target, "Activity"), target, name);
    }
}

// ─────────────────────────── tasks ───────────────────────────

public static class TaskCommands
{
    public static OracleItem Item(OracleCall c, TaskRow t, string? group = null)
    {
        var r = new EntityRef(EntityKind.Task, t.Id);
        var done = t.Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled;
        var overdue = !done && t.DueDate is { } d && d < c.Today;
        var sub = new List<string>();
        if (t.Priority >= ProjectPriority.High) sub.Add(c.T("Orc_Priority_" + t.Priority));
        if (t.DueDate is { } due) sub.Add(c.T(overdue ? "Orc_Overdue" : "Orc_Due", c.Env.Date(due) + (t.DueTime is { } tm ? " " + c.Env.Time(tm) : "")));
        if (!string.IsNullOrWhiteSpace(t.Tags)) sub.Add(t.Tags);
        var item = new OracleItem
        {
            Title = t.Title, Subtitle = string.Join(" · ", sub), Icon = done ? "Check" : "Check",
            Accent = overdue ? "Red" : t.Priority >= ProjectPriority.High ? "Amber" : "Blue",
            Kind = OracleEntity.Task, Ref = r, Group = group, IsMuted = done,
        };
        item.Actions.Add(new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true });
        if (!done)
            item.Actions.Add(new OracleAction
            {
                Label = c.T("Orc_Complete"), Icon = "Check", Kind = OracleActionKind.Invoke,
                Invoke = async ct =>
                {
                    await c.S.Tasks.SetStatusAsync(t.Id, TaskItemStatus.Completed, ct).ConfigureAwait(false);
                    return new OracleResult { Kind = OracleResultKind.Done, Headline = c.T("Orc_TaskCompleted", t.Title) };
                },
            });
        return item;
    }
}

/// <summary>"show overdue tasks", "tasks for today", "high priority tasks for Home Server", "tasks due within 7 days", "show its tasks".</summary>
public sealed class TaskListCommand : OracleCommandBase
{
    public override string Id => "tasks.list";
    public override string Module => "tasks";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Task];
    public override bool CanRefine => true;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["show overdue tasks", "tasks for today", "show tasks for Home Server", "tasks due within 7 days", "how many open tasks"];
    public override string[] ExamplesDe => ["zeige überfällige Aufgaben", "Aufgaben für heute"];
    public override string[] ExamplesAr => ["اعرض المهام المتأخرة", "مهام اليوم"];

    private static readonly EntityKind[] Scopes = [EntityKind.Project, EntityKind.Trip, EntityKind.Game, EntityKind.Place];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (!Is(p, OracleEntity.Task) || !IsQuery(p) || p.AllRelated) return 0;
        return p.Subject.Length == 0 ? 0.85 : 0.6;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (await UnresolvedTargetAsync(c, ct, x => $"show tasks for {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\"", Scopes).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, Scopes).ConfigureAwait(false);
        var view = p.HasFilter("overdue") ? TaskView.Overdue
            : p.HasFilter("completed") ? TaskView.Completed
            : p.HasFilter("pinned") ? TaskView.Pinned
            : p.Period is { Key: "today" } ? TaskView.Today
            : TaskView.All;
        var rows = await c.S.Tasks.QueryAsync(new TaskQuery(view, Text: p.Subject.Length > 0 ? p.Subject : p.Topic, Context: scope?.Ref, Take: 300), c.Now, ct).ConfigureAwait(false);
        IEnumerable<TaskRow> list = rows;
        if (view != TaskView.Completed) list = list.Where(t => t.Status is not (TaskItemStatus.Completed or TaskItemStatus.Cancelled));
        if (p.HasFilter("high")) list = list.Where(t => t.Priority >= ProjectPriority.High);
        if (p.Period is { } per && view != TaskView.Today)
            list = per.Key == "within" ? list.Where(t => t.DueDate is { } d && d <= per.To) : list.Where(t => t.DueDate is { } d && d >= per.From && d <= per.To);
        else if (p.HasFilter("upcoming") || p.HasFilter("due")) list = list.Where(t => t.DueDate is { } d && d >= c.Today && d <= c.Today.AddDays(14));
        var result = list.ToList();
        if (Counting(p)) return Count(c, "Orc_CountTasks", result.Count, NavigateAction(c, "tasks"));
        var items = result.Select(t => TaskCommands.Item(c, t)).ToList();
        var headline = scope is { } s ? c.T("Orc_ListTasksFor", items.Count, s.Name) : c.T("Orc_ListTasks", items.Count);
        return ListResult(c, headline, items, "Orc_NoTasks", scope is { } sc ? OpenAction(c, sc.Ref, sc.Ref.Kind == EntityKind.Project ? "Tasks" : null, false) : NavigateAction(c, "tasks", label: c.T("Orc_OpenTasksPage")), scope?.Ref, scope?.Name);
    }
}

/// <summary>"add task buy cables tomorrow", "add task to Home Server: label ports", "remind me to call mom tomorrow at 5pm".</summary>
public sealed class TaskCreateCommand : OracleCommandBase
{
    public override string Id => "tasks.create";
    public override string Module => "tasks";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Task];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["add task buy cables tomorrow", "add task to Home Server: label ports", "remind me to call the bank Friday at 10"];
    public override string[] ExamplesDe => ["neue Aufgabe Kabel kaufen morgen", "erinnere mich an Bank anrufen Freitag um 10"];
    public override string[] ExamplesAr => ["أضف مهمة شراء كابلات غدا", "ذكرني الاتصال بالبنك الجمعة الساعة 10"];

    private static readonly EntityKind[] Scopes = [EntityKind.Project, EntityKind.Trip, EntityKind.Game, EntityKind.Place];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (p.Verb == "remind" && (p.Payload ?? p.Subject).Length > 0) return 0.9;
        return p.Verb == "create" && Is(p, OracleEntity.Task) && (p.Payload ?? p.Subject).Length > 0 ? 0.9 : 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var title = (p.Payload ?? p.Subject).Trim();
        if (p.AmbiguousDate is { } amb) return CalendarCommands.AskDate(c, amb, p.Input.Original);
        if (await UnresolvedTargetAsync(c, ct, x => $"add task to {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\": {title}", Scopes).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, Scopes).ConfigureAwait(false);
        if (scope == null && p.Target == null && c.Context.Current is { } page && Scopes.Contains(page.Kind))
        {
            scope = (page, c.Context.CurrentName ?? string.Empty);
            c.Notes.Add(c.T("Orc_NoteFromPage", c.Context.CurrentName));
        }
        var remind = p.Verb == "remind";
        DateOnly? due = p.Period is { } per ? (per.Key == "within" ? per.To : per.From) : null;
        var time = p.Time;
        if (remind && due == null) due = time is { } tt && tt < TimeOnly.FromDateTime(c.Now) ? c.Today.AddDays(1) : c.Today;
        DateTime? reminder = remind && due is { } rd ? rd.ToDateTime(time ?? new TimeOnly(9, 0)) : null;
        var priority = p.HasFilter("high") ? ProjectPriority.High : ProjectPriority.Normal;

        var preview = new OraclePreview
        {
            Title = c.T(remind ? "Orc_CreateReminderTitle" : "Orc_CreateTaskTitle"),
            Icon = remind ? "Bell" : "Check",
            Fields = [(c.T("Orc_Field_Title"), title)],
            Confirm = c.ConfirmAction(async t =>
            {
                var task = await c.S.Tasks.CreateAsync(new TaskItemDraft(title, Priority: priority, DueDate: due, DueTime: time, ReminderAt: reminder, Context: scope?.Ref), t).ConfigureAwait(false);
                var r = new EntityRef(EntityKind.Task, task.Id);
                return new OracleResult
                {
                    Kind = OracleResultKind.Done, Headline = c.T("Orc_Created", title),
                    Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
                };
            }),
        };
        if (due is { } dd) preview.Fields.Add((c.T("Orc_Field_Due"), c.Env.Date(dd) + (time is { } t2 ? " " + c.Env.Time(t2) : "")));
        if (reminder is { } rem) preview.Fields.Add((c.T("Orc_Field_Reminder"), c.Env.Date(DateOnly.FromDateTime(rem)) + " " + c.Env.Time(TimeOnly.FromDateTime(rem))));
        if (priority != ProjectPriority.Normal) preview.Fields.Add((c.T("Orc_Field_Priority"), c.T("Orc_Priority_" + priority)));
        if (scope is { } s) preview.Fields.Add((c.T("Orc_Field_LinkedTo"), s.Name));
        return await c.ChangeAsync(preview, true, ct).ConfigureAwait(false);
    }
}

/// <summary>"complete task label ports", "mark buy milk as done", "reopen task X", "pin task X", "set task X to high priority", "move task X to Friday".</summary>
public sealed class TaskUpdateCommand : OracleCommandBase
{
    public override string Id => "tasks.update";
    public override string Module => "tasks";
    public override OracleIntent Intent => OracleIntent.Update;
    public override OracleEntity[] Entities => [OracleEntity.Task];
    public override OracleSafety Safety => OracleSafety.Update;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["complete task label ports", "mark buy milk as done", "reopen task label ports", "move task label ports to Friday", "set task label ports to high priority"];
    public override string[] ExamplesDe => ["erledige Aufgabe Kabel kaufen"];
    public override string[] ExamplesAr => ["انجز مهمة شراء كابلات"];

    private enum Op { Complete, Reopen, Pin, Unpin, Priority, Due }

    private static Op? OpOf(OracleParse p) => p.Verb switch
    {
        "complete" => Op.Complete,
        "reopen" => Op.Reopen,
        "pin" => Op.Pin,
        "unpin" => Op.Unpin,
        "set" when p.HasFilter("completed") => Op.Complete,
        "set" when p.HasFilter("unfinished") => Op.Reopen,
        "set" when p.HasFilter("high") => Op.Priority,
        "set" when p.Period != null => Op.Due,
        _ => null,
    };

    public override double Match(OracleParse p, OracleContext context)
    {
        if (OpOf(p) is null || NameOf(p).Length == 0) return 0;
        if (Mentions(p, OracleEntity.Task)) return 0.9;
        if (p.Entity != OracleEntity.None) return 0;
        return context.Module == "tasks" ? 0.8 : 0.6; // "mark buy milk as done": tasks are the usual thing to complete
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var op = OpOf(p)!.Value;
        var text = (p.Payload ?? p.Subject).Trim();
        if (text.Length == 0) text = NameOf(p);
        var r = await c.ResolveAsync(text, ct, EntityKind.Task).ConfigureAwait(false);
        if (r.Single is not { } s)
        {
            if (r.IsNone && !Mentions(p, OracleEntity.Task))
            {
                // not a task: maybe a game or project with the same verb
                var other = await c.ResolveAsync(text, ct, EntityKind.Game, EntityKind.Project).ConfigureAwait(false);
                if (other.Candidates.Count > 0)
                    return c.Choose(other, x => $"mark {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\" as completed", c.T("Orc_DidYouMean"));
            }
            return r.IsNone ? c.NotFound(text) : c.Choose(r, x => $"{Verb(op)} task \"{x.Name}\"{Suffix(c, op)}");
        }
        var id = s.Ref.Id;
        var name = s.Name;
        var due = p.Period?.From;
        var (title, icon, apply) = op switch
        {
            Op.Complete => (c.T("Orc_CompleteTitle", name), "Check", (Func<CancellationToken, Task>)(t => c.S.Tasks.SetStatusAsync(id, TaskItemStatus.Completed, t))),
            Op.Reopen => (c.T("Orc_ReopenTitle", name), "Refresh", t => c.S.Tasks.SetStatusAsync(id, TaskItemStatus.ToDo, t)),
            Op.Pin => (c.T("Orc_PinTitle", name), "Pin", t => c.S.Tasks.SetPinnedAsync(id, true, t)),
            Op.Unpin => (c.T("Orc_UnpinTitle", name), "Pin", t => c.S.Tasks.SetPinnedAsync(id, false, t)),
            Op.Due => (c.T("Orc_MoveTitle", name, c.Env.Date(due!.Value)), "Calendar", t => c.S.Tasks.SetDueAsync(id, due, p.Time, t)),
            _ => (c.T("Orc_PriorityTitle", name), "Flag", async t =>
            {
                var task = await c.S.Tasks.GetAsync(id, t).ConfigureAwait(false) ?? throw new InvalidOperationException("Task not found.");
                await c.S.Tasks.UpdateAsync(id, new TaskItemDraft(task.Title, task.Description, task.Status, ProjectPriority.High, task.StartDate, task.DueDate, task.DueTime,
                    task.ReminderAt, task.IsPinned, task.Notes, task.Tags, task.ParentTaskId,
                    task.ProjectId is { } pid ? new EntityRef(EntityKind.Project, pid) : task.ContextKind is { } k && task.ContextId is { } cid ? new EntityRef(k, cid) : null), t).ConfigureAwait(false);
            }),
        };
        return await c.ChangeAsync(new OraclePreview
        {
            Title = title,
            Icon = icon,
            Confirm = c.ConfirmAction(async t =>
            {
                await apply(t).ConfigureAwait(false);
                return new OracleResult
                {
                    Kind = OracleResultKind.Done, Headline = c.T("Orc_Updated", name),
                    Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = s.Ref, IsPrimary = true }],
                };
            }),
        }, r.IsExact, ct).ConfigureAwait(false);
    }

    private static string Verb(Op op) => op switch { Op.Complete => "complete", Op.Reopen => "reopen", Op.Pin => "pin", Op.Unpin => "unpin", _ => "set" };
    private static string Suffix(OracleCall c, Op op) => op switch
    {
        Op.Priority => " to high priority",
        Op.Due when c.P.Period is { } per => " to " + per.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => "",
    };
}

// ─────────────────────────── notes ───────────────────────────

public static class NoteCommands
{
    public static OracleItem Item(OracleCall c, NoteRow n, string? group = null)
    {
        var r = new EntityRef(EntityKind.Note, n.Id);
        return new OracleItem
        {
            Title = string.IsNullOrWhiteSpace(n.Title) ? c.T("Orc_Untitled") : n.Title,
            Subtitle = n.Excerpt.Length > 90 ? n.Excerpt[..90] + "…" : n.Excerpt,
            Value = c.Env.Date(DateOnly.FromDateTime(n.UpdatedAtUtc.ToLocalTime())),
            Icon = n.IsPinned ? "Pin" : "Notes", Accent = n.Accent ?? "Violet", Kind = OracleEntity.Note, Ref = r, Group = group,
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
        };
    }
}

/// <summary>"show notes about router", "pinned notes", "notes for Home Server", "notes tagged network".</summary>
public sealed class NoteListCommand : OracleCommandBase
{
    public override string Id => "notes.list";
    public override string Module => "notes";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Note];
    public override bool CanRefine => true;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["show notes about router", "pinned notes", "notes for Home Server", "notes tagged network"];
    public override string[] ExamplesDe => ["zeige Notizen über Router"];
    public override string[] ExamplesAr => ["اعرض الملاحظات عن الراوتر"];

    private static readonly EntityKind[] Scopes = [EntityKind.Project, EntityKind.Trip, EntityKind.Game, EntityKind.Place];

    public override double Match(OracleParse p, OracleContext context) => Is(p, OracleEntity.Note) && IsQuery(p) && !p.AllRelated ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (await UnresolvedTargetAsync(c, ct, x => $"show notes for {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\"", Scopes).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, Scopes).ConfigureAwait(false);
        var view = p.HasFilter("pinned") ? NoteView.Pinned : p.HasFilter("favorite") ? NoteView.Favorites : p.HasFilter("recent") ? NoteView.Recent : p.Tagged ? NoteView.ByTag : NoteView.All;
        var text = p.Tagged ? null : p.Topic ?? (p.Subject.Length > 0 ? p.Subject : null);
        var rows = await c.S.Notes.QueryAsync(new NoteQuery(view, text, Tag: p.Tagged ? p.Topic : null, Take: 300), ct).ConfigureAwait(false);
        IEnumerable<NoteRow> list = rows;
        if (scope is { } s) list = list.Where(n => n.Context == s.Ref);
        if (p.Period is { } per) list = list.Where(n => DateOnly.FromDateTime(n.UpdatedAtUtc.ToLocalTime()) is var d && d >= per.From && d <= per.To);
        var result = list.ToList();
        if (Counting(p)) return Count(c, "Orc_CountNotes", result.Count, NavigateAction(c, "notes"));
        var items = result.Select(n => NoteCommands.Item(c, n)).ToList();
        var headline = text != null ? c.T("Orc_ListNotesAbout", items.Count, text) : c.T("Orc_ListNotes", items.Count);
        return ListResult(c, headline, items, "Orc_NoNotes", NavigateAction(c, "notes", label: c.T("Orc_OpenNotesPage")), scope?.Ref, scope?.Name);
    }
}

/// <summary>"add note to Cyberpunk: finish Panam missions" (a game journal entry), "new note: router settings", "notiere …".</summary>
public sealed class NoteCreateCommand : OracleCommandBase
{
    public override string Id => "notes.create";
    public override string Module => "notes";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Note];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["add note to Cyberpunk: finish Panam missions", "new note: router admin is 192.168.1.1", "add note to Home Server: order rack"];
    public override string[] ExamplesDe => ["neue Notiz zu Cyberpunk: Panam Missionen"];
    public override string[] ExamplesAr => ["أضف ملاحظة إلى Cyberpunk: إنهاء مهمات Panam"];

    private static readonly EntityKind[] Scopes = [EntityKind.Game, EntityKind.Project, EntityKind.Trip, EntityKind.Place];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (p.Verb != "create" || !Is(p, OracleEntity.Note)) return 0;
        return (p.Payload ?? p.Subject).Length > 0 ? 0.9 : 0.5;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var text = (p.Payload ?? p.Subject).Trim();
        if (text.Length == 0) return new OracleResult { Kind = OracleResultKind.Clarify, Headline = c.T("Orc_NoteNeedsText"), Suggestions = ["add note: " + c.T("Orc_ExampleNote")] };
        if (await UnresolvedTargetAsync(c, ct, x => $"add note to {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\": {text}", Scopes).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, Scopes).ConfigureAwait(false);
        if (scope == null && p.Target == null && c.Context.Current is { } page && Scopes.Contains(page.Kind))
        {
            scope = (page, c.Context.CurrentName ?? string.Empty);
            c.Notes.Add(c.T("Orc_NoteFromPage", c.Context.CurrentName));
        }
        var toGame = scope is { Ref.Kind: EntityKind.Game };
        var title = text.Length > 60 ? text[..60].TrimEnd() + "…" : text;
        var preview = new OraclePreview
        {
            Title = c.T(toGame ? "Orc_CreateJournalTitle" : "Orc_CreateNoteTitle"),
            Icon = toGame ? "Journal" : "Notes",
            Fields = [(c.T("Orc_Field_Text"), text)],
            Confirm = c.ConfirmAction(async t =>
            {
                if (toGame)
                {
                    await c.S.Games.AddJournalEntryAsync(scope!.Value.Ref.Id, text, t).ConfigureAwait(false);
                    return new OracleResult
                    {
                        Kind = OracleResultKind.Done, Headline = c.T("Orc_JournalAdded", scope.Value.Name), Focus = scope.Value.Ref, FocusName = scope.Value.Name,
                        Actions = [new OracleAction { Label = c.T("Orc_Journal"), Icon = "Journal", Kind = OracleActionKind.Open, Target = scope.Value.Ref, Tab = "Journal", IsPrimary = true }],
                    };
                }
                var note = await c.S.Notes.CreateAsync(new NoteDraft(title, text, Context: scope?.Ref), t).ConfigureAwait(false);
                var r = new EntityRef(EntityKind.Note, note.Id);
                return new OracleResult
                {
                    Kind = OracleResultKind.Done, Headline = c.T("Orc_Created", title),
                    Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
                };
            }),
        };
        if (scope is { } s) preview.Fields.Add((c.T("Orc_Field_LinkedTo"), s.Name));
        return await c.ChangeAsync(preview, true, ct).ConfigureAwait(false);
    }
}

// ─────────────────────────── calendar ───────────────────────────

public static class CalendarCommands
{
    public static OracleItem Item(OracleCall c, CalendarItem e, string? group = null)
    {
        var when = e.IsAllDay ? c.Env.Date(e.StartDate) : c.Env.Date(e.StartDate) + " " + c.Env.Time(TimeOnly.FromDateTime(e.Start));
        var item = new OracleItem
        {
            Title = e.Title, Subtitle = e.Subtitle ?? e.Location, Value = when, Icon = e.Icon, Accent = e.Accent,
            Kind = e.Kind switch { CalendarItemKind.Task => OracleEntity.Task, CalendarItemKind.Bill => OracleEntity.Bill, CalendarItemKind.Trip or CalendarItemKind.Itinerary => OracleEntity.Trip, _ => OracleEntity.Event },
            Ref = e.Ref, Group = group, IsMuted = e.IsDone,
        };
        item.Actions.Add(new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = e.Ref, IsPrimary = true });
        return item;
    }

    /// <summary>"03/04" reads two ways: offer both as exact commands.</summary>
    public static OracleResult AskDate(OracleCall c, (DateOnly DayFirst, DateOnly MonthFirst) amb, string original)
    {
        string Replace(DateOnly d)
        {
            var tokens = original.Split(' ');
            for (var i = 0; i < tokens.Length; i++)
                if (tokens[i].Count(ch => ch == '/') >= 1 && tokens[i].Any(char.IsDigit)) tokens[i] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return string.Join(' ', tokens);
        }
        return new OracleResult
        {
            Kind = OracleResultKind.Clarify,
            Headline = c.T("Orc_AmbiguousDate"),
            Confidence = OracleConfidence.Medium,
            Items =
            [
                new OracleItem { Title = c.Env.Date(amb.DayFirst), Subtitle = c.T("Orc_DayFirst"), Icon = "Calendar", Actions = [new OracleAction { Label = c.T("Orc_Choose"), Icon = "Check", Kind = OracleActionKind.Run, Command = Replace(amb.DayFirst), IsPrimary = true }] },
                new OracleItem { Title = c.Env.Date(amb.MonthFirst), Subtitle = c.T("Orc_MonthFirst"), Icon = "Calendar", Actions = [new OracleAction { Label = c.T("Orc_Choose"), Icon = "Check", Kind = OracleActionKind.Run, Command = Replace(amb.MonthFirst), IsPrimary = true }] },
            ],
        };
    }
}

/// <summary>"what's on tomorrow", "show events this week", "agenda next week", "was steht morgen an", "ماذا لدي اليوم".</summary>
public sealed class AgendaCommand : OracleCommandBase
{
    public override string Id => "calendar.agenda";
    public override string Module => "calendar";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Event];
    public override bool CanRefine => true;
    public override string[] Examples => ["what's on tomorrow", "show events this week", "what do I have today", "events next month"];
    public override string[] ExamplesDe => ["was steht morgen an", "Termine diese Woche"];
    public override string[] ExamplesAr => ["ماذا لدي اليوم", "مواعيد الاسبوع القادم"];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (Is(p, OracleEntity.Event) && IsQuery(p) && p.Subject.Length == 0) return 0.85;
        if (p.Question == "what" && p.Entities.Count == 0 && p.Metric == null && p.Tool == null && p.Verb is null or "show") return p.Period != null ? 0.85 : 0.55;
        if (p.Module == "calendar" && p.Period != null && p.Verb is null or "show") return 0.8;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var events = Is(p, OracleEntity.Event);
        var per = p.Period ?? (events ? new OraclePeriod(c.Today, c.Today.AddDays(7), "week") : new OraclePeriod(c.Today, c.Today, "today"));
        var sources = events ? CalendarSources.Events : CalendarSources.All;
        var rows = await c.S.Agenda.GetItemsAsync(per.From.ToDateTime(TimeOnly.MinValue), per.To.AddDays(1).ToDateTime(TimeOnly.MinValue), sources, ct).ConfigureAwait(false);
        var list = rows.Where(e => !e.IsDone || e.Kind != CalendarItemKind.Task).OrderBy(e => e.Start).ToList();
        if (!events && per.From <= c.Today && per.To >= c.Today)
        {
            // today's view also shows overdue tasks, which are not on the calendar
            var overdue = await c.S.Tasks.QueryAsync(new TaskQuery(TaskView.Overdue, Take: 20), c.Now, ct).ConfigureAwait(false);
            var items0 = list.Select(e => CalendarCommands.Item(c, e, c.Env.Date(e.StartDate))).ToList();
            items0.InsertRange(0, overdue.Where(t => t.DueDate < c.Today).Select(t => TaskCommands.Item(c, t, c.T("Orc_Group_Overdue"))));
            return Finish(c, per, items0);
        }
        if (Counting(p)) return Count(c, "Orc_CountEvents", list.Count, NavigateAction(c, "calendar"));
        return Finish(c, per, list.Select(e => CalendarCommands.Item(c, e, c.Env.Date(e.StartDate))).ToList());
    }

    private static OracleResult Finish(OracleCall c, OraclePeriod per, List<OracleItem> items)
    {
        var range = per.IsSingleDay ? c.Env.Date(per.From) : c.Env.Date(per.From) + " – " + c.Env.Date(per.To);
        return ListResult(c, c.T("Orc_Agenda", items.Count, range), items, "Orc_AgendaEmpty", NavigateAction(c, "calendar", label: c.T("Orc_OpenCalendar")));
    }
}

/// <summary>"create event dentist tomorrow at 10", "meeting with Sam Friday 15:00", "Termin Zahnarzt morgen um 10".</summary>
public sealed class EventCreateCommand : OracleCommandBase
{
    public override string Id => "calendar.create";
    public override string Module => "calendar";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Event];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["create event dentist tomorrow at 10", "add meeting with Sam Friday 15:00", "plan event Rome flight 2026-10-12"];
    public override string[] ExamplesDe => ["neuer Termin Zahnarzt morgen um 10"];
    public override string[] ExamplesAr => ["أضف موعد طبيب الأسنان غدا الساعة 10"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Verb is "create" && Is(p, OracleEntity.Event) && (p.Payload ?? p.Subject).Length > 0 ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (p.AmbiguousDate is { } amb) return CalendarCommands.AskDate(c, amb, p.Input.Original);
        var title = (p.Payload ?? p.Subject).Trim();
        var eventWord = p.Input.Tokens.FirstOrDefault(t => t.Norm is "meeting" or "appointment" or "termin" or "موعد");
        if (eventWord != null && !title.Contains(eventWord.Text, StringComparison.OrdinalIgnoreCase) && eventWord.Norm is "meeting" or "appointment") title = eventWord.Text + " " + title;
        var date = p.Period?.From ?? c.Today;
        if (p.Period == null) c.Notes.Add(c.T("Orc_NoDateToday"));
        var allDay = p.Time == null;
        var start = date.ToDateTime(p.Time ?? TimeOnly.MinValue);
        var end = allDay ? start.AddDays(1) : start.AddHours(1);
        EntityRef? context = c.Context.Current is { Kind: EntityKind.Project or EntityKind.Trip or EntityKind.Game } page ? page : null;
        var preview = new OraclePreview
        {
            Title = c.T("Orc_CreateEventTitle"),
            Icon = "Calendar",
            Fields = [(c.T("Orc_Field_Title"), title), (c.T("Orc_Field_When"), c.Env.Date(date) + (allDay ? " · " + c.T("Orc_AllDay") : " " + c.Env.Time(p.Time!.Value)))],
            Confirm = c.ConfirmAction(async t =>
            {
                var ev = await c.S.Calendar.CreateEventAsync(new EventDraft(title, start, end, allDay, Context: context), t).ConfigureAwait(false);
                var r = new EntityRef(EntityKind.Event, ev.Id);
                return new OracleResult
                {
                    Kind = OracleResultKind.Done, Headline = c.T("Orc_Created", title),
                    Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
                };
            }),
        };
        if (context != null) preview.Fields.Add((c.T("Orc_Field_LinkedTo"), c.Context.CurrentName ?? ""));
        return await c.ChangeAsync(preview, true, ct).ConfigureAwait(false);
    }
}

/// <summary>"when is the dentist", "when is my Italy trip", "wann ist der Zahnarzt".</summary>
public sealed class WhenCommand : OracleCommandBase
{
    public override string Id => "calendar.when";
    public override string Module => "calendar";
    public override OracleIntent Intent => OracleIntent.Search;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["when is the dentist", "when is my Italy trip"];
    public override string[] ExamplesDe => ["wann ist der Zahnarzt"];
    public override string[] ExamplesAr => ["متى موعد طبيب الأسنان"];

    public override double Match(OracleParse p, OracleContext context) => p.Question == "when" && NameOf(p).Length > 0 ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var text = NameOf(c.P);
        if (Is(c.P, OracleEntity.Trip) || c.P.TargetKind == OracleEntity.Trip)
            return await TripWhen(c, text, ct).ConfigureAwait(false) ?? c.NotFound(text);
        var events = await c.S.Agenda.SearchEventsAsync(text, c.Now.Date, c.Now.Date.AddYears(1), ct).ConfigureAwait(false);
        if (events.Count > 0)
        {
            var next = events.OrderBy(e => e.Start).First();
            var items = events.OrderBy(e => e.Start).Take(10).Select(e => CalendarCommands.Item(c, e)).ToList();
            return new OracleResult { Kind = OracleResultKind.List, Headline = c.T("Orc_NextOccurrence", next.Title, CalendarCommands.Item(c, next).Value), Items = items };
        }
        return await TripWhen(c, text, ct).ConfigureAwait(false) ?? c.NotFound(text);
    }

    private static async Task<OracleResult?> TripWhen(OracleCall c, string text, CancellationToken ct)
    {
        var r = await c.ResolveAsync(text, ct, EntityKind.Trip).ConfigureAwait(false);
        if (r.Single is not { } s) return r.IsNone ? null : c.Choose(r, x => $"when is trip \"{x.Name}\"");
        var trip = await c.S.Travel.GetTripSummaryAsync(s.Ref.Id, ct).ConfigureAwait(false);
        if (trip == null) return null;
        var when = trip.StartDate is { } a ? c.Env.Date(a) + (trip.EndDate is { } b && b != a ? " – " + c.Env.Date(b) : "") : c.T("Orc_NoDates");
        return new OracleResult
        {
            Kind = OracleResultKind.Message, Headline = c.T("Orc_TripWhen", trip.Name, when), Focus = s.Ref, FocusName = trip.Name,
            Actions = [new OracleAction { Label = c.T("Orc_OpenName", trip.Name), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = s.Ref, IsPrimary = true }],
        };
    }
}
