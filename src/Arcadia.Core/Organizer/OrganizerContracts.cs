using Arcadia.Core.Projects;

namespace Arcadia.Core.Organizer;

// ───────────────────────── calendar ─────────────────────────

public sealed record EventDraft(
    string Title,
    DateTime Start,
    DateTime End,
    bool IsAllDay = false,
    string? Location = null,
    int? PlaceId = null,
    int? CategoryId = null,
    int? ReminderMinutes = null,
    RecurrenceFrequency Frequency = RecurrenceFrequency.None,
    int Interval = 1,
    WeekdayMask Weekdays = WeekdayMask.None,
    DateOnly? RecursUntil = null,
    string? Description = null,
    string? Notes = null,
    string Tags = "",
    EntityRef? Context = null);

/// <summary>How far an edit or deletion of a recurring occurrence reaches.</summary>
public enum RecurrenceScope { ThisEvent, ThisAndFuture, AllEvents }

public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CalendarCategory> SaveCategoryAsync(int? id, string name, string icon, string accent, CancellationToken cancellationToken = default);
    /// <summary>Events of the category keep existing, without a category.</summary>
    Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default);

    Task<CalendarEvent?> GetEventAsync(int id, CancellationToken cancellationToken = default);
    Task<CalendarEvent> CreateEventAsync(EventDraft draft, CancellationToken cancellationToken = default);
    /// <summary>
    /// Edits an event. For a recurring event, <paramref name="occurrenceDate"/> names the occurrence the user opened:
    /// ThisEvent stores an exception, ThisAndFuture splits the series there, AllEvents changes the series (keeping each
    /// occurrence's date when only times or details change). Returns the id of the event that now holds the change.
    /// </summary>
    Task<int> UpdateEventAsync(int id, EventDraft draft, RecurrenceScope scope = RecurrenceScope.AllEvents, DateOnly? occurrenceDate = null, CancellationToken cancellationToken = default);
    Task DeleteEventAsync(int id, RecurrenceScope scope = RecurrenceScope.AllEvents, DateOnly? occurrenceDate = null, CancellationToken cancellationToken = default);
    /// <summary>Events (with exceptions) that can have an occurrence overlapping the range: one-off events in it and every series still running.</summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsForRangeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
}

/// <summary>What the calendar may show, each switchable without touching data.</summary>
[Flags]
public enum CalendarSources
{
    None = 0, Events = 1, Tasks = 2, Projects = 4, Travel = 8, Gaming = 16, Finance = 32,
    All = Events | Tasks | Projects | Travel | Gaming | Finance,
}

public enum CalendarItemKind { Event, Task, ProjectDeadline, Trip, Itinerary, Bill }

/// <summary>
/// One entry on the calendar, projected from its owner (an event occurrence, a task due date, a project target date, a
/// trip's days, an itinerary item). Nothing is copied: <see cref="Ref"/> opens the original.
/// </summary>
public sealed record CalendarItem(
    CalendarItemKind Kind,
    EntityRef Ref,
    string Title,
    DateTime Start,
    DateTime End,
    bool IsAllDay,
    string Accent,
    string Icon,
    string? Subtitle = null,
    DateOnly? OccurrenceDate = null,
    bool IsRecurring = false,
    bool IsDone = false,
    int? ReminderMinutes = null,
    string? Location = null,
    EntityRef? Context = null)
{
    public DateOnly StartDate => DateOnly.FromDateTime(Start);
    /// <summary>Last day the item is visible on (inclusive).</summary>
    public DateOnly EndDate => IsAllDay ? DateOnly.FromDateTime(End.AddTicks(-1) < Start ? Start : End.AddTicks(-1)) : DateOnly.FromDateTime(End > Start ? End.AddTicks(-1) : Start);
}

/// <summary>Everything dated across Arcadia, for calendars, the Today view and future command engines.</summary>
public interface ICalendarQueryService
{
    Task<IReadOnlyList<CalendarItem>> GetItemsAsync(DateTime from, DateTime to, CalendarSources sources, CancellationToken cancellationToken = default);
    /// <summary>Events matching text in title, description, location, tags or category, optionally within dates.</summary>
    Task<IReadOnlyList<CalendarItem>> SearchEventsAsync(string text, DateTime? from = null, DateTime? to = null, CancellationToken cancellationToken = default);
}

// ───────────────────────── tasks ─────────────────────────

public sealed record TaskItemDraft(
    string Title,
    string? Description = null,
    TaskItemStatus Status = TaskItemStatus.ToDo,
    ProjectPriority Priority = ProjectPriority.Normal,
    DateOnly? StartDate = null,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    DateTime? ReminderAt = null,
    bool IsPinned = false,
    string? Notes = null,
    string Tags = "",
    int? ParentTaskId = null,
    EntityRef? Context = null); // project, another record, or null for a free-standing task

public enum TaskView { Today, Upcoming, All, Overdue, Completed, Pinned, ByProject }

public enum TaskSort { Smart, DueDate, Priority, Title, Updated, Created }

public sealed record TaskQuery(
    TaskView View = TaskView.All,
    string? Text = null,
    int? ProjectId = null,
    EntityRef? Context = null,
    ProjectPriority? Priority = null,
    string? Tag = null,
    TaskSort Sort = TaskSort.Smart,
    int Skip = 0,
    int Take = 500,
    int UpcomingDays = 14); // Upcoming = due within this many days after today

public sealed record TaskRow(
    int Id,
    string Title,
    TaskItemStatus Status,
    ProjectPriority Priority,
    DateOnly? StartDate,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    DateTime? ReminderAt,
    bool IsPinned,
    string Tags,
    EntityRef? Context,
    int? ParentTaskId,
    int SubtaskCount,
    int SubtasksDone,
    bool HasDetails,
    DateTime? CompletedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime CreatedAtUtc)
{
    public bool IsDone => Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled;
}

public sealed record TaskCounts(int Today, int Overdue, int Upcoming, int Pinned, int Open, int CompletedToday);

/// <summary>Read side of tasks (safe for dashboards and a future command engine).</summary>
public interface ITaskQueryService
{
    Task<IReadOnlyList<TaskRow>> QueryAsync(TaskQuery query, DateTime now, CancellationToken cancellationToken = default);
    Task<TaskCounts> GetCountsAsync(DateTime now, CancellationToken cancellationToken = default);
    /// <summary>Open tasks whose reminder falls in (from, to] (for the reminder service).</summary>
    Task<IReadOnlyList<TaskItem>> GetRemindersAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
}

public interface ITaskStore : ITaskQueryService
{
    Task<TaskItem?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<TaskItem> CreateAsync(TaskItemDraft draft, CancellationToken cancellationToken = default);
    Task UpdateAsync(int id, TaskItemDraft draft, CancellationToken cancellationToken = default);
    Task SetStatusAsync(int id, TaskItemStatus status, CancellationToken cancellationToken = default);
    Task SetPinnedAsync(int id, bool pinned, CancellationToken cancellationToken = default);
    Task SetDueAsync(int id, DateOnly? due, TimeOnly? time, CancellationToken cancellationToken = default);
    /// <summary>Deletes the task and its subtasks.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(int parentId, CancellationToken cancellationToken = default);
    /// <summary>The subtasks of several tasks as list rows, in their order (one query for a whole page).</summary>
    Task<IReadOnlyList<TaskRow>> GetSubtasksForAsync(IReadOnlyCollection<int> parentIds, CancellationToken cancellationToken = default);
}

// ───────────────────────── notes ─────────────────────────

public sealed record NoteDraft(string Title, string Content = "", string Tags = "", EntityRef? Context = null, bool IsPinned = false, bool IsFavorite = false);

public enum NoteView { All, Recent, Pinned, Favorites, ByModule, ByTag }

public enum NoteSort { Updated, Created, Title }

public sealed record NoteQuery(NoteView View = NoteView.All, string? Text = null, EntityKind? Module = null, bool GeneralOnly = false, string? Tag = null,
    NoteSort Sort = NoteSort.Updated, int Skip = 0, int Take = 300);

public sealed record NoteRow(int Id, string Title, string Excerpt, bool IsPinned, bool IsFavorite, string Tags, EntityRef? Context, string? Accent, string? Icon,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc, int Length);

public sealed record TagCount(string Tag, int Count);

/// <summary>Read side of notes.</summary>
public interface INoteQueryService
{
    Task<IReadOnlyList<NoteRow>> QueryAsync(NoteQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken cancellationToken = default);
}

public interface INoteStore : INoteQueryService
{
    Task<Note?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<Note> CreateAsync(NoteDraft draft, CancellationToken cancellationToken = default);
    /// <summary>Autosave target. Keeps a few earlier versions (at most one per few minutes). Returns the new modification time.</summary>
    Task<DateTime> SaveAsync(int id, string title, string content, string tags, CancellationToken cancellationToken = default);
    Task SetPinnedAsync(int id, bool pinned, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default);
    Task SetContextAsync(int id, EntityRef? context, CancellationToken cancellationToken = default);
    Task SetAppearanceAsync(int id, string? accent, string? icon, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NoteRevision>> GetRevisionsAsync(int id, CancellationToken cancellationToken = default);
}

// ───────────────────────── shared ─────────────────────────

/// <summary>What an <see cref="EntityRef"/> points at, for context chips and link labels.</summary>
public sealed record EntityInfo(EntityRef Ref, string Name, string Icon, string Accent, bool Exists = true);

public interface IEntityDirectory
{
    Task<IReadOnlyDictionary<EntityRef, EntityInfo>> DescribeAsync(IEnumerable<EntityRef> refs, CancellationToken cancellationToken = default);
    /// <summary>Records of one kind whose names match, for pickers (most recent first).</summary>
    Task<IReadOnlyList<EntityInfo>> FindAsync(EntityKind kind, string? text, int take = 50, CancellationToken cancellationToken = default);
}

/// <summary>What Quick Capture creates. Expense goes to Finance (<c>IQuickExpenseService</c>), the others to the organizer.</summary>
public enum CaptureKind { Task, Note, Event, Expense }

public sealed record CaptureResult(CaptureKind Kind, int Id, string Title, DateOnly? Date, TimeOnly? Time, bool DateWasAmbiguous);

/// <summary>Creates a task, note or event from one line of text (dates understood by <see cref="QuickDateParser"/>).</summary>
public interface IQuickCaptureService
{
    Task<CaptureResult> CaptureAsync(CaptureKind kind, string text, DateTime now, EntityRef? context = null, CancellationToken cancellationToken = default);
}
