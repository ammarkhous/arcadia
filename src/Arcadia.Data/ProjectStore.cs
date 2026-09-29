using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

/// <summary>SQLite persistence for projects, their tasks, notes, links and activity history.</summary>
public sealed class ProjectStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : IProjectStore
{
    private static readonly string[] Themes =
        ["mountains-dusk", "lake-sunset", "forest-night", "castle-violet", "mountains-snow", "golden-realm", "space-nebula", "desert-sunset", "lake-storm", "neon-city"];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ─────────────────────────── projects ───────────────────────────

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Projects.AsNoTracking()
            .Select(p => new
            {
                p,
                Counted = p.Tasks.Count(t => t.Status != TaskItemStatus.Cancelled),
                Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Completed),
                Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => ToSummary(r.p, r.Counted, r.Done, r.Open)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static ProjectSummary ToSummary(Project p, int counted, int done, int open)
    {
        int progress = p.ProgressMode == ProgressMode.Manual ? Math.Clamp(p.ManualProgress, 0, 100)
            : counted == 0 ? 0 : (int)Math.Round(done * 100.0 / counted, MidpointRounding.AwayFromZero);
        return new ProjectSummary(p.Id, p.Name, p.Description, p.Status, p.Priority, p.StartDate, p.TargetDate, p.Accent, p.Icon, p.ArtworkTheme, p.ArtworkSeed,
            p.Tags, p.IsPinned, progress, counted, open, p.UpdatedAtUtc, p.LastOpenedAtUtc, p.FolderPath);
    }

    public async Task<ProjectStats> GetStatsAsync(DateOnly today, int deadlineDays, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var byStatus = await db.Projects.GroupBy(p => p.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        int Count(ProjectStatus s) => byStatus.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        var remaining = await db.Tasks.CountAsync(t => t.ProjectId != null && t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled
                                                             && t.Project!.Status != ProjectStatus.Archived, cancellationToken).ConfigureAwait(false);
        var deadlines = (await GetDeadlinesAsync(today, deadlineDays, cancellationToken).ConfigureAwait(false)).Count;
        return new ProjectStats(Count(ProjectStatus.Planning), Count(ProjectStatus.Active), Count(ProjectStatus.OnHold), Count(ProjectStatus.Completed), Count(ProjectStatus.Archived), remaining, deadlines);
    }

    public async Task<IReadOnlyList<Deadline>> GetDeadlinesAsync(DateOnly today, int days, CancellationToken cancellationToken = default)
    {
        var until = today.AddDays(days);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.TargetDate != null && p.TargetDate <= until && p.Status != ProjectStatus.Completed && p.Status != ProjectStatus.Archived)
            .Select(p => new Deadline(p.Id, p.Name, p.Accent, null, p.Name, p.TargetDate!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId != null && t.DueDate != null && t.DueDate <= until && t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled
                        && t.Project!.Status != ProjectStatus.Archived && t.Project.Status != ProjectStatus.Completed)
            .Select(t => new Deadline(t.ProjectId!.Value, t.Project!.Name, t.Project.Accent, t.Id, t.Title, t.DueDate!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return projects.Concat(tasks).OrderBy(d => d.Due).ThenBy(d => d.TaskId.HasValue).ToList();
    }

    public async Task<Project?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.AsNoTracking()
            .Include(p => p.Tasks).Include(p => p.Notes).Include(p => p.Links)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        if (project == null) return null;
        project.Activity = await db.ProjectActivity.AsNoTracking().Where(a => a.ProjectId == id)
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Rebuild the one-level hierarchy in memory for convenience.
        foreach (var t in project.Tasks.Where(t => t.ParentTaskId != null))
            project.Tasks.FirstOrDefault(p => p.Id == t.ParentTaskId)?.Subtasks.Add(t);
        return project;
    }

    public async Task<Project> CreateAsync(ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        Validate(draft);
        var now = Now;
        var project = new Project { CreatedAtUtc = now, UpdatedAtUtc = now, ArtworkSeed = Random.Shared.Next(1, 10_000) };
        Apply(project, draft);
        if (string.IsNullOrWhiteSpace(draft.ArtworkTheme)) project.ArtworkTheme = Themes[project.ArtworkSeed % Themes.Length];
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Log(db, project.Id, ProjectActivityKind.ProjectCreated, project.Name);
        if (project.FolderPath != null) Log(db, project.Id, ProjectActivityKind.FolderSet, project.FolderPath);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexProjectAsync(db, project, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public Task UpdateAsync(int id, ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        Validate(draft);
        return MutateAsync(id, async (db, p) =>
        {
            var oldStatus = p.Status;
            var oldName = p.Name;
            var oldFolder = p.FolderPath;
            Apply(p, draft);
            if (draft.Status != oldStatus) ApplyStatus(db, p, draft.Status, oldStatus);
            else Log(db, p.Id, ProjectActivityKind.ProjectUpdated, p.Name);
            if (p.FolderPath != oldFolder && p.FolderPath != null) Log(db, p.Id, ProjectActivityKind.FolderSet, p.FolderPath);
            if (oldName != p.Name)
            {
                // documents are searchable by their project's name
                foreach (var docId in await db.DocumentProjects.Where(d => d.ProjectId == p.Id).Select(d => d.DocumentId).ToListAsync(cancellationToken).ConfigureAwait(false))
                    await SearchIndex.IndexDocumentAsync(db, docId, null, cancellationToken).ConfigureAwait(false);
            }
        }, reindex: true, cancellationToken);
    }

    public Task SetStatusAsync(int id, ProjectStatus status, CancellationToken cancellationToken = default) =>
        MutateAsync(id, (db, p) =>
        {
            if (p.Status != status) ApplyStatus(db, p, status, p.Status);
            return Task.CompletedTask;
        }, reindex: false, cancellationToken);

    public Task ArchiveAsync(int id, CancellationToken cancellationToken = default) => SetStatusAsync(id, ProjectStatus.Archived, cancellationToken);

    public Task RestoreAsync(int id, CancellationToken cancellationToken = default) =>
        MutateAsync(id, (db, p) =>
        {
            if (p.Status == ProjectStatus.Archived) ApplyStatus(db, p, ProjectRules.Restore(p), p.Status);
            return Task.CompletedTask;
        }, reindex: false, cancellationToken);

    /// <summary>Archiving keeps everything; it only changes the status (remembering the previous one).</summary>
    private void ApplyStatus(ArcadiaDbContext db, Project p, ProjectStatus status, ProjectStatus previous)
    {
        p.Status = status;
        if (status == ProjectStatus.Archived)
        {
            p.StatusBeforeArchive = previous;
            p.ArchivedAtUtc = Now;
            Log(db, p.Id, ProjectActivityKind.ProjectArchived, null);
        }
        else if (previous == ProjectStatus.Archived)
        {
            p.ArchivedAtUtc = null;
            Log(db, p.Id, ProjectActivityKind.ProjectRestored, status.ToString());
        }
        else
        {
            Log(db, p.Id, ProjectActivityKind.StatusChanged, status.ToString());
        }
    }

    public Task SetPinnedAsync(int id, bool pinned, CancellationToken cancellationToken = default) =>
        MutateAsync(id, (_, p) => { p.IsPinned = pinned; return Task.CompletedTask; }, reindex: false, cancellationToken, touch: false);

    public Task SetProgressAsync(int id, ProgressMode mode, int manualProgress, CancellationToken cancellationToken = default) =>
        MutateAsync(id, (_, p) =>
        {
            p.ProgressMode = mode;
            p.ManualProgress = Math.Clamp(manualProgress, 0, 100);
            return Task.CompletedTask;
        }, reindex: false, cancellationToken);

    public Task SetDescriptionAsync(int id, string? description, CancellationToken cancellationToken = default) =>
        MutateAsync(id, (_, p) => { p.Description = string.IsNullOrWhiteSpace(description) ? null : description; return Task.CompletedTask; }, reindex: true, cancellationToken);

    public Task MarkOpenedAsync(int id, CancellationToken cancellationToken = default) =>
        MutateAsync(id, (_, p) => { p.LastOpenedAtUtc = Now; return Task.CompletedTask; }, reindex: false, cancellationToken, touch: false);

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var docIds = await db.DocumentProjects.Where(d => d.ProjectId == id).Select(d => d.DocumentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteProjectAsync(db, id, cancellationToken).ConfigureAwait(false);
        foreach (var docId in docIds) await SearchIndex.IndexDocumentAsync(db, docId, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────── tasks ───────────────────────────

    public async Task<TaskItem> AddTaskAsync(int projectId, TaskDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateTask(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.FindAsync([projectId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        await EnsureParentAsync(db, projectId, draft.ParentTaskId, null, cancellationToken).ConfigureAwait(false);
        var now = Now;
        var order = await db.Tasks.Where(t => t.ProjectId == projectId).MaxAsync(t => (int?)t.SortOrder, cancellationToken).ConfigureAwait(false) ?? 0;
        var task = new TaskItem { ProjectId = projectId, CreatedAtUtc = now, UpdatedAtUtc = now, SortOrder = order + 1 };
        ApplyTask(task, draft);
        if (task.Status == TaskItemStatus.Completed) task.CompletedAtUtc = now;
        db.Tasks.Add(task);
        project.UpdatedAtUtc = now;
        Log(db, projectId, ProjectActivityKind.TaskCreated, task.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexTaskAsync(db, task, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task UpdateTaskAsync(int taskId, TaskDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateTask(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.Include(t => t.Project).FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        await EnsureParentAsync(db, task.ProjectId, draft.ParentTaskId, task.Id, cancellationToken).ConfigureAwait(false);
        var oldStatus = task.Status;
        ApplyTask(task, draft);
        task.Status = oldStatus;
        ChangeTaskStatus(db, task, draft.Status);
        task.UpdatedAtUtc = Now;
        if (task.Project != null) task.Project.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexTaskAsync(db, task, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTaskStatusAsync(int taskId, TaskItemStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.Include(t => t.Project).FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        ChangeTaskStatus(db, task, status);
        if (task.Project != null) task.Project.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ChangeTaskStatus(ArcadiaDbContext db, TaskItem task, TaskItemStatus status)
    {
        if (task.Status == status) return;
        var wasDone = task.IsDone;
        ProjectRules.SetStatus(task, status, Now);
        if (status == TaskItemStatus.Completed) Log(db, task.ProjectId, ProjectActivityKind.TaskCompleted, task.Title);
        else if (wasDone && task.IsOpen) Log(db, task.ProjectId, ProjectActivityKind.TaskReopened, task.Title);
    }

    public async Task DeleteTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.Include(t => t.Subtasks).FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken).ConfigureAwait(false);
        if (task == null) return;
        foreach (var sub in task.Subtasks) await SearchIndex.DeleteAsync(db, SearchResultKind.Task, sub.Id, cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Task, task.Id, cancellationToken).ConfigureAwait(false);
        db.Tasks.RemoveRange(task.Subtasks);
        db.Tasks.Remove(task);
        Log(db, task.ProjectId, ProjectActivityKind.TaskDeleted, task.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task EnsureParentAsync(ArcadiaDbContext db, int? projectId, int? parentId, int? selfId, CancellationToken ct)
    {
        if (parentId is not { } pid) return;
        if (pid == selfId) throw new ArgumentException("A task cannot be its own parent.");
        var parent = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == pid, ct).ConfigureAwait(false)
                     ?? throw new ArgumentException("The parent task does not exist.");
        if (!ProjectRules.CanBeParent(parent, projectId)) throw new ArgumentException("Subtasks can only be one level deep.");
        if (selfId is { } sid && await db.Tasks.AnyAsync(t => t.ParentTaskId == sid, ct).ConfigureAwait(false))
            throw new ArgumentException("A task with subtasks cannot become a subtask.");
    }

    // ─────────────────────────── notes ───────────────────────────

    public async Task<Note> AddNoteAsync(int projectId, string title, string content = "", CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.FindAsync([projectId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var now = Now;
        var note = new Note { ProjectId = projectId, Title = Clip(string.IsNullOrWhiteSpace(title) ? "Untitled" : title, 300), Content = content, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Notes.Add(note);
        project.UpdatedAtUtc = now;
        Log(db, projectId, ProjectActivityKind.NoteAdded, note.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexNoteAsync(db, note, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return note;
    }

    /// <summary>Autosave: writes the note exactly as typed (no activity entry — edits are not events).</summary>
    public async Task<DateTime> SaveNoteAsync(int noteId, string title, string content, string tags, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var note = await db.Notes.Include(n => n.Project).FirstOrDefaultAsync(n => n.Id == noteId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        note.Title = Clip(string.IsNullOrWhiteSpace(title) ? "Untitled" : title, 300);
        note.Content = content;
        note.Tags = Clip(TagText.Normalize(tags), 512);
        note.UpdatedAtUtc = Now;
        if (note.Project != null) note.Project.UpdatedAtUtc = note.UpdatedAtUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexNoteAsync(db, note, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return note.UpdatedAtUtc;
    }

    public async Task SetNotePinnedAsync(int noteId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Notes.Where(n => n.Id == noteId).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsPinned, pinned), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteNoteAsync(int noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var note = await db.Notes.FindAsync([noteId], cancellationToken).ConfigureAwait(false);
        if (note == null) return;
        db.Notes.Remove(note);
        Log(db, note.ProjectId, ProjectActivityKind.NoteDeleted, note.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Note, noteId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────── links ───────────────────────────

    public async Task<ProjectLink> AddLinkAsync(int projectId, LinkDraft draft, CancellationToken cancellationToken = default)
    {
        var url = ValidLink(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.FindAsync([projectId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var link = new ProjectLink { ProjectId = projectId, CreatedAtUtc = Now };
        ApplyLink(link, draft, url);
        db.ProjectLinks.Add(link);
        project.UpdatedAtUtc = Now;
        Log(db, projectId, ProjectActivityKind.LinkAdded, link.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexLinkAsync(db, link, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return link;
    }

    public async Task UpdateLinkAsync(int linkId, LinkDraft draft, CancellationToken cancellationToken = default)
    {
        var url = ValidLink(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var link = await db.ProjectLinks.FindAsync([linkId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        ApplyLink(link, draft, url);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexLinkAsync(db, link, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLinkPinnedAsync(int linkId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.ProjectLinks.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPinned, pinned), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteLinkAsync(int linkId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var link = await db.ProjectLinks.FindAsync([linkId], cancellationToken).ConfigureAwait(false);
        if (link == null) return;
        db.ProjectLinks.Remove(link);
        Log(db, link.ProjectId, ProjectActivityKind.LinkRemoved, link.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Link, linkId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectActivity>> GetActivityAsync(int projectId, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ProjectActivity.AsNoTracking().Where(a => a.ProjectId == projectId)
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private async Task MutateAsync(int id, Func<ArcadiaDbContext, Project, Task> change, bool reindex, CancellationToken ct, bool touch = true)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var project = await db.Projects.FindAsync([id], ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        await change(db, project).ConfigureAwait(false);
        if (touch) project.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (reindex) await SearchIndex.IndexProjectAsync(db, project, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Logs to the project's history when the task/note belongs to a project (free-standing ones have none).</summary>
    internal static void Log(ArcadiaDbContext db, int? projectId, ProjectActivityKind kind, string? subject, DateTime? at = null)
    {
        if (projectId is { } id) Log(db, id, kind, subject, at);
    }

    internal static ProjectActivity Log(ArcadiaDbContext db, int projectId, ProjectActivityKind kind, string? subject, DateTime? at = null)
    {
        var a = new ProjectActivity { ProjectId = projectId, Kind = kind, Subject = subject == null ? null : Clip(subject, 400), CreatedAtUtc = at ?? DateTime.UtcNow };
        db.ProjectActivity.Add(a);
        return a;
    }

    private static void Apply(Project p, ProjectDraft d)
    {
        p.Name = Clip(d.Name, 200);
        p.Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description;
        p.Status = d.Status;
        p.Priority = d.Priority;
        p.StartDate = d.StartDate;
        p.TargetDate = d.TargetDate;
        p.Accent = string.IsNullOrWhiteSpace(d.Accent) ? "Blue" : Clip(d.Accent, 16);
        p.Icon = string.IsNullOrWhiteSpace(d.Icon) ? "Projects" : Clip(d.Icon, 32);
        if (!string.IsNullOrWhiteSpace(d.ArtworkTheme)) p.ArtworkTheme = Clip(d.ArtworkTheme, 64);
        p.Tags = Clip(TagText.Normalize(d.Tags), 512);
        p.FolderPath = Arcadia.Core.Documents.PathRules.Normalize(d.FolderPath);
    }

    private static void ApplyTask(TaskItem t, TaskDraft d)
    {
        t.Title = Clip(d.Title, 300);
        t.Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description;
        t.Status = d.Status;
        t.Priority = d.Priority;
        t.DueDate = d.DueDate;
        t.Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes;
        t.Tags = Clip(TagText.Normalize(d.Tags), 512);
        t.ParentTaskId = d.ParentTaskId;
    }

    private static void ApplyLink(ProjectLink l, LinkDraft d, string url)
    {
        l.Url = url;
        l.Title = Clip(string.IsNullOrWhiteSpace(d.Title) ? LinkRules.DefaultTitle(url) : d.Title, 300);
        l.Description = string.IsNullOrWhiteSpace(d.Description) ? null : Clip(d.Description, 2000);
        l.Tags = Clip(TagText.Normalize(d.Tags), 512);
    }

    private static void Validate(ProjectDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) throw new ArgumentException("A project name is required.");
        if (d.StartDate is { } s && d.TargetDate is { } t && t < s) throw new ArgumentException("The target date is before the start date.");
    }

    private static void ValidateTask(TaskDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Title)) throw new ArgumentException("A task title is required.");
    }

    private static string ValidLink(LinkDraft d) =>
        LinkRules.TryNormalize(d.Url, out var url) ? url : throw new ArgumentException("That is not a valid web address.");

    internal static string Clip(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max];
    }
}
