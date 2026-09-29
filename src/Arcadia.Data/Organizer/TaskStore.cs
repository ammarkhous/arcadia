using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Organizer;

/// <summary>
/// Arcadia-wide tasks. Project tasks are the same rows the Projects module edits through <see cref="ProjectStore"/>: both
/// apply <see cref="ProjectRules.SetStatus"/>, keep one level of subtasks, log project activity and update the search
/// index in the same transaction.
/// </summary>
public sealed class TaskStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : ITaskStore
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<TaskItem?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tasks.AsNoTracking().Include(t => t.Project).Include(t => t.Subtasks.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(int parentId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tasks.AsNoTracking().Where(t => t.ParentTaskId == parentId).OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskRow>> GetSubtasksForAsync(IReadOnlyCollection<int> parentIds, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = parentIds.ToList();
        var subs = await db.Tasks.AsNoTracking().Where(t => t.ParentTaskId != null && ids.Contains(t.ParentTaskId.Value))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return subs.Select(s => new TaskRow(s.Id, s.Title, s.Status, s.Priority, s.StartDate, s.DueDate, s.DueTime, s.ReminderAt, s.IsPinned, s.Tags, s.Context, s.ParentTaskId,
            0, 0, !string.IsNullOrWhiteSpace(s.Description) || !string.IsNullOrWhiteSpace(s.Notes), s.CompletedAtUtc, s.UpdatedAtUtc, s.CreatedAtUtc)).ToList();
    }

    public async Task<TaskItem> CreateAsync(TaskItemDraft draft, CancellationToken cancellationToken = default)
    {
        Validate(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = Now;
        var task = new TaskItem { CreatedAtUtc = now, UpdatedAtUtc = now };
        Apply(task, draft);
        await ApplyContextAsync(db, task, draft, cancellationToken).ConfigureAwait(false);
        await ProjectStore.EnsureParentAsync(db, task.ProjectId, draft.ParentTaskId, null, cancellationToken).ConfigureAwait(false);
        task.SortOrder = (await db.Tasks.Where(t => t.ProjectId == task.ProjectId && t.ParentTaskId == task.ParentTaskId)
            .MaxAsync(t => (int?)t.SortOrder, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        if (task.Status == TaskItemStatus.Completed) task.CompletedAtUtc = now;
        db.Tasks.Add(task);
        ProjectStore.Log(db, task.ProjectId, ProjectActivityKind.TaskCreated, task.Title);
        await TouchProjectAsync(db, task.ProjectId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexTaskAsync(db, task, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task UpdateAsync(int id, TaskItemDraft draft, CancellationToken cancellationToken = default)
    {
        Validate(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The task no longer exists.");
        var oldStatus = task.Status;
        var oldProject = task.ProjectId;
        Apply(task, draft);
        task.Status = oldStatus;
        if (task.ParentTaskId == null) await ApplyContextAsync(db, task, draft, cancellationToken).ConfigureAwait(false);
        await ProjectStore.EnsureParentAsync(db, task.ProjectId, draft.ParentTaskId, task.Id, cancellationToken).ConfigureAwait(false);
        ChangeStatus(db, task, draft.Status);
        task.UpdatedAtUtc = Now;
        // subtasks follow their parent to its new context
        if (task.ParentTaskId == null && (oldProject != task.ProjectId))
            await db.Tasks.Where(t => t.ParentTaskId == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ProjectId, task.ProjectId).SetProperty(t => t.ContextKind, task.ContextKind).SetProperty(t => t.ContextId, task.ContextId), cancellationToken)
                .ConfigureAwait(false);
        await TouchProjectAsync(db, task.ProjectId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexTaskAsync(db, task, cancellationToken).ConfigureAwait(false);
        foreach (var sub in await db.Tasks.Where(t => t.ParentTaskId == task.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
            await SearchIndex.IndexTaskAsync(db, sub, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(int id, TaskItemStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The task no longer exists.");
        ChangeStatus(db, task, status);
        task.UpdatedAtUtc = Now;
        await TouchProjectAsync(db, task.ProjectId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPinnedAsync(int id, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.Tasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsPinned, pinned).SetProperty(t => t.UpdatedAtUtc, Now), cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException("The task no longer exists.");
    }

    public async Task SetDueAsync(int id, DateOnly? due, TimeOnly? dueTime, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The task no longer exists.");
        if (due != null && task.StartDate is { } start && due < start) throw new ArgumentException("The due date cannot be before the start date.");
        task.DueDate = due;
        task.DueTime = due == null ? null : dueTime;
        task.UpdatedAtUtc = Now;
        await TouchProjectAsync(db, task.ProjectId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.Tasks.Include(t => t.Subtasks).FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (task == null) return;
        foreach (var sub in task.Subtasks) await SearchIndex.DeleteAsync(db, SearchResultKind.Task, sub.Id, cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Task, task.Id, cancellationToken).ConfigureAwait(false);
        db.Tasks.RemoveRange(task.Subtasks);
        db.Tasks.Remove(task);
        ProjectStore.Log(db, task.ProjectId, ProjectActivityKind.TaskDeleted, task.Title);
        await TouchProjectAsync(db, task.ProjectId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── queries ─────────────────────────

    public async Task<IReadOnlyList<TaskRow>> QueryAsync(TaskQuery query, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(now);
        var nowTime = TimeOnly.FromDateTime(now);
        var q = db.Tasks.AsNoTracking();
        var completed = TaskItemStatus.Completed;
        var cancelled = TaskItemStatus.Cancelled;
        q = query.View switch
        {
            TaskView.Today => q.Where(t => t.Status != completed && t.Status != cancelled && t.DueDate != null && t.DueDate <= today),
            TaskView.Upcoming => q.Where(t => t.Status != completed && t.Status != cancelled && t.DueDate > today && t.DueDate <= today.AddDays(query.UpcomingDays)),
            TaskView.Overdue => q.Where(t => t.Status != completed && t.Status != cancelled && t.DueDate != null
                                             && (t.DueDate < today || (t.DueDate == today && t.DueTime != null && t.DueTime < nowTime))),
            TaskView.Completed => q.Where(t => t.Status == completed || t.Status == cancelled),
            TaskView.Pinned => q.Where(t => t.IsPinned && t.Status != completed && t.Status != cancelled),
            TaskView.ByProject => q.Where(t => t.ProjectId != null && t.ParentTaskId == null && t.Status != completed && t.Status != cancelled),
            _ => q.Where(t => t.ParentTaskId == null),
        };
        if (query.ProjectId is { } pid) q = q.Where(t => t.ProjectId == pid);
        if (query.Context is { } ctx)
        {
            if (ctx.Kind == EntityKind.Project) q = q.Where(t => t.ProjectId == ctx.Id);
            else q = q.Where(t => t.ContextKind == ctx.Kind && t.ContextId == ctx.Id);
        }
        if (query.Priority is { } prio) q = q.Where(t => t.Priority == prio);
        if (!string.IsNullOrWhiteSpace(query.Tag)) { var tag = query.Tag.Trim(); q = q.Where(t => EF.Functions.Like(t.Tags, "%" + tag + "%")); }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var ids = await TextMatchesAsync(db, query.Text, cancellationToken).ConfigureAwait(false);
            q = q.Where(t => ids.Contains(t.Id));
        }

        var rank = q.Select(t => new
        {
            Task = t,
            Rank = t.Priority == ProjectPriority.Critical ? 0 : t.Priority == ProjectPriority.High ? 1 : t.Priority == ProjectPriority.Normal ? 2 : 3,
        });
        rank = query.Sort switch
        {
            TaskSort.Priority => rank.OrderBy(x => x.Rank).ThenBy(x => x.Task.DueDate == null).ThenBy(x => x.Task.DueDate).ThenBy(x => x.Task.Id),
            TaskSort.Title => rank.OrderBy(x => x.Task.Title).ThenBy(x => x.Task.Id),
            TaskSort.Updated => rank.OrderByDescending(x => x.Task.UpdatedAtUtc),
            TaskSort.Created => rank.OrderByDescending(x => x.Task.CreatedAtUtc),
            TaskSort.DueDate => rank.OrderBy(x => x.Task.DueDate == null).ThenBy(x => x.Task.DueDate).ThenBy(x => x.Task.DueTime == null).ThenBy(x => x.Task.DueTime).ThenBy(x => x.Rank),
            _ when query.View == TaskView.Completed => rank.OrderByDescending(x => x.Task.CompletedAtUtc ?? x.Task.UpdatedAtUtc),
            // smart: open before done, then by when it is due (overdue first), then pinned, priority and the user's order
            _ => rank.OrderBy(x => x.Task.Status == completed || x.Task.Status == cancelled)
                     .ThenBy(x => x.Task.DueDate == null).ThenBy(x => x.Task.DueDate).ThenBy(x => x.Task.DueTime == null).ThenBy(x => x.Task.DueTime)
                     .ThenByDescending(x => x.Task.IsPinned).ThenBy(x => x.Rank).ThenBy(x => x.Task.SortOrder).ThenBy(x => x.Task.Id),
        };

        return await rank.Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 5000))
            .Select(x => new TaskRow(
                x.Task.Id, x.Task.Title, x.Task.Status, x.Task.Priority, x.Task.StartDate, x.Task.DueDate, x.Task.DueTime, x.Task.ReminderAt, x.Task.IsPinned, x.Task.Tags,
                x.Task.ProjectId != null ? new EntityRef(EntityKind.Project, x.Task.ProjectId.Value)
                    : x.Task.ContextKind != null && x.Task.ContextId != null ? new EntityRef(x.Task.ContextKind.Value, x.Task.ContextId.Value) : null,
                x.Task.ParentTaskId,
                db.Tasks.Count(s => s.ParentTaskId == x.Task.Id),
                db.Tasks.Count(s => s.ParentTaskId == x.Task.Id && s.Status == completed),
                (x.Task.Description != null && x.Task.Description != "") || (x.Task.Notes != null && x.Task.Notes != ""),
                x.Task.CompletedAtUtc, x.Task.UpdatedAtUtc, x.Task.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskCounts> GetCountsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(now);
        var nowTime = TimeOnly.FromDateTime(now);
        var week = today.AddDays(7);
        var dayStartUtc = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        // one pass over the open tasks' due fields (small projection), counted in memory
        var open = await db.Tasks.AsNoTracking().Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
            .Select(t => new { t.DueDate, t.DueTime, t.IsPinned }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var c = new
        {
            Today = open.Count(t => t.DueDate == today),
            Overdue = open.Count(t => t.DueDate < today || (t.DueDate == today && t.DueTime != null && t.DueTime < nowTime)),
            Upcoming = open.Count(t => t.DueDate > today && t.DueDate <= week),
            Pinned = open.Count(t => t.IsPinned),
            Open = open.Count,
        };
        var done = await db.Tasks.CountAsync(t => t.Status == TaskItemStatus.Completed && t.CompletedAtUtc >= dayStartUtc, cancellationToken).ConfigureAwait(false);
        return new TaskCounts(c.Today, c.Overdue, c.Upcoming, c.Pinned, c.Open, done);
    }

    public async Task<IReadOnlyList<TaskItem>> GetRemindersAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tasks.AsNoTracking()
            .Where(t => t.ReminderAt != null && t.ReminderAt > from && t.ReminderAt <= to && t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Task ids whose title, description, notes or tags match (FTS, so thousands of tasks stay fast).</summary>
    internal static async Task<List<int>> TextMatchesAsync(ArcadiaDbContext db, string text, CancellationToken ct)
    {
        if (FtsQuery.Build(text) is not { } fts) return [];
        return await SearchIndex.MatchIdsAsync(db, fts, SearchResultKind.Task, 5000, ct).ConfigureAwait(false);
    }

    // ───────────────────────── helpers ─────────────────────────

    private void ChangeStatus(ArcadiaDbContext db, TaskItem task, TaskItemStatus status)
    {
        if (task.Status == status) return;
        var wasDone = task.IsDone;
        ProjectRules.SetStatus(task, status, Now);
        if (status == TaskItemStatus.Completed) ProjectStore.Log(db, task.ProjectId, ProjectActivityKind.TaskCompleted, task.Title);
        else if (wasDone && task.IsOpen) ProjectStore.Log(db, task.ProjectId, ProjectActivityKind.TaskReopened, task.Title);
    }

    private static void Apply(TaskItem t, TaskItemDraft d)
    {
        t.Title = d.Title.Trim();
        t.Description = Blank(d.Description);
        t.Status = d.Status;
        t.Priority = d.Priority;
        t.StartDate = d.StartDate;
        t.DueDate = d.DueDate;
        t.DueTime = d.DueDate == null ? null : d.DueTime;
        t.ReminderAt = d.ReminderAt;
        t.IsPinned = d.IsPinned;
        t.Notes = Blank(d.Notes);
        t.Tags = TagText.Normalize(d.Tags);
        t.ParentTaskId = d.ParentTaskId;
    }

    /// <summary>A subtask always shares its parent's context; a top-level task takes the draft's.</summary>
    private static async Task ApplyContextAsync(ArcadiaDbContext db, TaskItem task, TaskItemDraft draft, CancellationToken ct)
    {
        EntityRef? ctx = draft.Context;
        if (draft.ParentTaskId is { } pid)
        {
            var parent = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == pid, ct).ConfigureAwait(false) ?? throw new ArgumentException("The parent task does not exist.");
            ctx = parent.Context;
        }
        task.ProjectId = null;
        task.ContextKind = null;
        task.ContextId = null;
        if (ctx is not { } c) return;
        if (c.Kind == EntityKind.Project)
        {
            if (!await db.Projects.AnyAsync(p => p.Id == c.Id, ct).ConfigureAwait(false)) throw new ArgumentException("The project no longer exists.");
            task.ProjectId = c.Id;
        }
        else if (c.Kind is EntityKind.Task or EntityKind.Note or EntityKind.Event) throw new ArgumentException("A task can belong to a project, trip, place, game, document or diagnostic session.");
        else
        {
            task.ContextKind = c.Kind;
            task.ContextId = c.Id;
        }
    }

    private static async Task TouchProjectAsync(ArcadiaDbContext db, int? projectId, CancellationToken ct)
    {
        if (projectId is not { } id) return;
        var project = await db.Projects.FindAsync([id], ct).ConfigureAwait(false);
        if (project != null) project.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void Validate(TaskItemDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Title)) throw new ArgumentException("A task needs a title.");
        if (d.Title.Trim().Length > 300) throw new ArgumentException("The title is too long (300 characters at most).");
        if (d.Description?.Length > 8000 || d.Notes?.Length > 8000) throw new ArgumentException("The text is too long (8000 characters at most).");
        if (d.StartDate is { } s && d.DueDate is { } due && due < s) throw new ArgumentException("The due date cannot be before the start date.");
        if (d.DueTime != null && d.DueDate == null) throw new ArgumentException("A due time needs a due date.");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
