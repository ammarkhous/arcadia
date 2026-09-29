using Arcadia.Core.Projects;

namespace Arcadia.Core.Organizer;

public enum TaskItemStatus { ToDo, InProgress, Blocked, Completed, Cancelled }

/// <summary>
/// One task for all of Arcadia (Phase 6 unified the Phase 4 project tasks into this table). A task belongs to a project
/// (<see cref="ProjectId"/>, which keeps project progress and cascade deletion), to another record through
/// <see cref="ContextKind"/>/<see cref="ContextId"/> (trip, game, diagnostic session, document…), or to nothing.
/// Dates and times are the user's local calendar values ("floating"), not UTC instants.
/// </summary>
public sealed class TaskItem
{
    public int Id { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    /// <summary>Non-project context (never <see cref="EntityKind.Project"/>: that is <see cref="ProjectId"/>).</summary>
    public EntityKind? ContextKind { get; set; }
    public int? ContextId { get; set; }
    /// <summary>One level of subtasks: a subtask's parent is always a top-level task.</summary>
    public int? ParentTaskId { get; set; }
    public TaskItem? ParentTask { get; set; }
    public List<TaskItem> Subtasks { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.ToDo;
    public ProjectPriority Priority { get; set; } = ProjectPriority.Normal;
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Optional time on <see cref="DueDate"/>; without it the task is due "some time that day".</summary>
    public TimeOnly? DueTime { get; set; }
    /// <summary>Local wall-clock moment of the reminder, independent of the due date.</summary>
    public DateTime? ReminderAt { get; set; }
    public bool IsPinned { get; set; }
    public string? Notes { get; set; }
    public string Tags { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public bool IsDone => Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled;
    public bool IsOpen => !IsDone;

    /// <summary>The record this task is about: its project, its other context, or null.</summary>
    public EntityRef? Context => ProjectId is { } p ? new EntityRef(EntityKind.Project, p) : ContextKind is { } k && ContextId is { } id ? new EntityRef(k, id) : null;
}

/// <summary>
/// One note for all of Arcadia (Phase 6 unified the Phase 4 project notes into this table): general, or about a project
/// (<see cref="ProjectId"/>) or another record (<see cref="ContextKind"/>/<see cref="ContextId"/>). Content is plain text with
/// optional lightweight Markdown; <c>arcadia://kind/id</c> links inside it navigate within the app.
/// </summary>
public sealed class Note
{
    public int Id { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public EntityKind? ContextKind { get; set; }
    public int? ContextId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public string Tags { get; set; } = string.Empty;
    /// <summary>Optional accent tone from Arcadia's palette and optional Arcadia icon name.</summary>
    public string? Accent { get; set; }
    public string? Icon { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public EntityRef? Context => ProjectId is { } p ? new EntityRef(EntityKind.Project, p) : ContextKind is { } k && ContextId is { } id ? new EntityRef(k, id) : null;
}

/// <summary>A lightweight earlier version of a note (a few per note, taken at most every few minutes while editing).</summary>
public sealed class NoteRevision
{
    public int Id { get; set; }
    public int NoteId { get; set; }
    public Note? Note { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
}

public enum RecurrenceFrequency { None, Daily, Weekly, Monthly, Yearly }

/// <summary>Weekdays as flags (a weekly rule may repeat on several days).</summary>
[Flags]
public enum WeekdayMask
{
    None = 0, Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
}

/// <summary>A user-editable event category. Colour comes only from Arcadia's accent palette.</summary>
public sealed class CalendarCategory
{
    public int Id { get; set; }
    /// <summary>Built-in categories have a key (their name is localized until the user renames them).</summary>
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string Icon { get; set; } = "Calendar";
    public string Accent { get; set; } = "Blue";
    public int SortOrder { get; set; }
}

/// <summary>
/// A calendar event. A recurring event is one row with its rule (frequency, interval, weekdays, optional end); occurrences
/// are computed, never stored. Changes to single occurrences are <see cref="CalendarEventException"/> rows.
/// Start/End are local wall-clock values; an all-day event uses dates only (End is inclusive).
/// </summary>
public sealed class CalendarEvent
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public int? PlaceId { get; set; }
    public int? CategoryId { get; set; }
    public CalendarCategory? Category { get; set; }
    public bool IsAllDay { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Minutes before the start (0 = at the start); null = no reminder.</summary>
    public int? ReminderMinutes { get; set; }
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; } = 1;
    public WeekdayMask Weekdays { get; set; }
    /// <summary>Last date an occurrence may start on (inclusive); null = forever.</summary>
    public DateOnly? RecursUntil { get; set; }
    public string? Notes { get; set; }
    public string Tags { get; set; } = string.Empty;
    public EntityKind? ContextKind { get; set; }
    public int? ContextId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<CalendarEventException> Exceptions { get; set; } = [];

    public bool IsRecurring => Frequency != RecurrenceFrequency.None;
    public TimeSpan Duration => End - Start;
    public EntityRef? Context => ContextKind is { } k && ContextId is { } id ? new EntityRef(k, id) : null;
}

/// <summary>One occurrence of a recurring event that was cancelled or changed ("this event only").</summary>
public sealed class CalendarEventException
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public CalendarEvent? Event { get; set; }
    /// <summary>The date the occurrence would have started on under the rule.</summary>
    public DateOnly OriginalDate { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public string? Title { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
}
