using Arcadia.Core.Projects;

namespace Arcadia.Core.Organizer;

/// <summary>When a task counts as due, overdue or upcoming (pure, testable).</summary>
public static class TaskRules
{
    /// <summary>The moment a task is due: its time on its due date, or the end of that day when it has no time.</summary>
    public static DateTime? DueMoment(DateOnly? due, TimeOnly? time) =>
        due is { } d ? (time is { } t ? d.ToDateTime(t) : d.ToDateTime(TimeOnly.MaxValue)) : null;

    /// <summary>Open and past its due moment (a task due today without a time is not overdue until tomorrow).</summary>
    public static bool IsOverdue(TaskItemStatus status, DateOnly? due, TimeOnly? time, DateTime now) =>
        !IsClosed(status) && DueMoment(due, time) is { } m && m < now;

    public static bool IsDueToday(TaskItemStatus status, DateOnly? due, DateTime now) =>
        !IsClosed(status) && due == DateOnly.FromDateTime(now);

    /// <summary>Today's list: open tasks due today or earlier.</summary>
    public static bool BelongsToToday(TaskItemStatus status, DateOnly? due, DateTime now) =>
        !IsClosed(status) && due is { } d && d <= DateOnly.FromDateTime(now);

    public static bool IsUpcoming(TaskItemStatus status, DateOnly? due, DateTime now, int days) =>
        !IsClosed(status) && due is { } d && d > DateOnly.FromDateTime(now) && d <= DateOnly.FromDateTime(now).AddDays(days);

    public static bool IsClosed(TaskItemStatus status) => status is TaskItemStatus.Completed or TaskItemStatus.Cancelled;

    /// <summary>Smart order: overdue and due first (by moment), then pinned, then priority, then the rest by creation.</summary>
    public static int SmartRank(ProjectPriority priority) => priority switch
    {
        ProjectPriority.Critical => 0,
        ProjectPriority.High => 1,
        ProjectPriority.Normal => 2,
        _ => 3,
    };
}

public enum ReminderSource { Event, Task }

/// <summary>A reminder that is (or was) due at <see cref="TriggerAt"/> (local time).</summary>
public sealed record ReminderDue(ReminderSource Source, int Id, DateOnly? OccurrenceDate, DateTime TriggerAt, DateTime? StartsAt, string Title, string? Location)
{
    /// <summary>Identifies the reminder instance (one per occurrence) for de-duplication.</summary>
    public string Key => $"{Source}:{Id}:{OccurrenceDate?.DayNumber ?? 0}:{TriggerAt.Ticks}";
}

/// <summary>
/// Works out which reminders fall in a time window. The reminder service asks for the next one and sleeps until then,
/// instead of polling. Event reminders are computed per occurrence (recurring events never materialise rows).
/// </summary>
public static class ReminderPlanner
{
    public static IReadOnlyList<ReminderDue> Due(IEnumerable<CalendarEvent> events, IEnumerable<TaskItem> tasks, DateTime fromExclusive, DateTime toInclusive)
    {
        var list = new List<ReminderDue>();
        if (toInclusive <= fromExclusive) return list;
        foreach (var ev in events)
        {
            if (ev.ReminderMinutes is not { } minutes || minutes < 0) continue;
            var lead = TimeSpan.FromMinutes(minutes);
            // occurrences near (from + lead, to + lead]; a day of slack covers all-day events, which remind relative to 09:00
            foreach (var o in Recurrence.Expand(ev, fromExclusive + lead - TimeSpan.FromDays(1), toInclusive + lead + TimeSpan.FromDays(1)))
            {
                var start = o.IsAllDay ? o.Start.Date.AddHours(9) : o.Start; // all-day reminders refer to 09:00 of the day
                var trigger = start - lead;
                if (trigger > fromExclusive && trigger <= toInclusive)
                    list.Add(new ReminderDue(ReminderSource.Event, ev.Id, ev.IsRecurring ? o.OriginalDate : null, trigger, o.Start, o.Title, o.Location));
            }
        }
        foreach (var t in tasks)
        {
            if (t.ReminderAt is not { } at || t.IsDone) continue;
            if (at > fromExclusive && at <= toInclusive)
                list.Add(new ReminderDue(ReminderSource.Task, t.Id, null, at, TaskRules.DueMoment(t.DueDate, t.DueTime) is { } m && t.DueTime != null ? m : null, t.Title, null));
        }
        list.Sort((a, b) => a.TriggerAt.CompareTo(b.TriggerAt));
        return list;
    }
}
