using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Travel;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Organizer;

/// <summary>
/// The calendar's view of Arcadia: event occurrences plus dates owned by other modules, projected on the fly —
/// task due dates, project target dates, trips (their whole span) and itinerary items with an exact time. Nothing is
/// duplicated; every item carries an <see cref="EntityRef"/> back to its owner. "Gaming" collects events in the Gaming
/// category and tasks/events about a game, so it can be switched separately.
/// </summary>
public sealed class CalendarQueryService(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : ICalendarQueryService
{
    public async Task<IReadOnlyList<CalendarItem>> GetItemsAsync(DateTime from, DateTime to, CalendarSources sources, CancellationToken cancellationToken = default)
    {
        var items = new List<CalendarItem>();
        if (to <= from || sources == CalendarSources.None) return items;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var fromDate = DateOnly.FromDateTime(from);
        var lastDate = DateOnly.FromDateTime(to.AddTicks(-1));

        if ((sources & (CalendarSources.Events | CalendarSources.Gaming)) != 0)
        {
            foreach (var ev in await CalendarStore.EventsForRange(db, from, to).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                var gaming = IsGaming(ev.Category?.Key, ev.ContextKind);
                if (!sources.HasFlag(gaming ? CalendarSources.Gaming : CalendarSources.Events)) continue;
                foreach (var o in Recurrence.Expand(ev, from, to)) items.Add(FromOccurrence(o));
            }
        }

        if ((sources & (CalendarSources.Tasks | CalendarSources.Gaming)) != 0)
        {
            var tasks = await db.Tasks.AsNoTracking()
                .Where(t => t.DueDate != null && t.DueDate >= fromDate && t.DueDate <= lastDate && t.Status != TaskItemStatus.Cancelled)
                .Select(t => new { t.Id, t.Title, t.DueDate, t.DueTime, t.Status, t.Priority, t.ProjectId, t.ContextKind, t.ContextId, t.ReminderAt })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var t in tasks)
            {
                var gaming = t.ContextKind == EntityKind.Game;
                if (!sources.HasFlag(gaming ? CalendarSources.Gaming : CalendarSources.Tasks)) continue;
                var date = t.DueDate!.Value;
                var start = t.DueTime is { } time ? date.ToDateTime(time) : date.ToDateTime(TimeOnly.MinValue);
                var ctx = t.ProjectId is { } p ? new EntityRef(EntityKind.Project, p) : EntityRef.From(t.ContextKind?.ToString(), t.ContextId);
                items.Add(new CalendarItem(CalendarItemKind.Task, new EntityRef(EntityKind.Task, t.Id), t.Title, start, t.DueTime != null ? start.AddMinutes(30) : start.AddDays(1),
                    t.DueTime == null, t.Priority is ProjectPriority.Critical ? "Red" : t.Priority is ProjectPriority.High ? "Amber" : "Purple", "Tasks",
                    IsDone: t.Status == TaskItemStatus.Completed, Context: ctx));
            }
        }

        if (sources.HasFlag(CalendarSources.Projects))
        {
            var projects = await db.Projects.AsNoTracking()
                .Where(p => p.TargetDate != null && p.TargetDate >= fromDate && p.TargetDate <= lastDate && p.Status != ProjectStatus.Archived)
                .Select(p => new { p.Id, p.Name, p.TargetDate, p.Accent, p.Status })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var p in projects)
            {
                var start = p.TargetDate!.Value.ToDateTime(TimeOnly.MinValue);
                items.Add(new CalendarItem(CalendarItemKind.ProjectDeadline, new EntityRef(EntityKind.Project, p.Id), p.Name, start, start.AddDays(1), true, p.Accent, "Flag",
                    IsDone: p.Status == ProjectStatus.Completed));
            }
        }

        if (sources.HasFlag(CalendarSources.Travel))
        {
            var trips = await db.Trips.AsNoTracking()
                .Where(t => t.StartDate != null && t.StartDate <= lastDate && (t.EndDate ?? t.StartDate) >= fromDate && t.Status != TripStatus.Cancelled)
                .Select(t => new { t.Id, t.Name, t.StartDate, t.EndDate, t.Accent, t.Status })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var t in trips)
            {
                var start = t.StartDate!.Value.ToDateTime(TimeOnly.MinValue);
                var end = (t.EndDate ?? t.StartDate.Value).AddDays(1).ToDateTime(TimeOnly.MinValue);
                items.Add(new CalendarItem(CalendarItemKind.Trip, new EntityRef(EntityKind.Trip, t.Id), t.Name, start, end, true, t.Accent, "Suitcase",
                    IsDone: t.Status == TripStatus.Completed));
            }
            // itinerary entries with an exact time (the others stay in the trip's dossier)
            var tripIds = trips.Select(t => t.Id).ToList();
            if (tripIds.Count > 0)
            {
                var rows = await db.ItineraryItems.AsNoTracking()
                    .Where(i => i.Slot == TimeSlot.Exact && i.Time != null && tripIds.Contains(i.Day!.TripId))
                    .Select(i => new { i.Id, i.Title, i.Time, i.IsDone, i.Day!.Index, i.Day.TripId, Place = i.Place != null ? i.Place.Name : null })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var i in rows)
                {
                    var trip = trips.First(t => t.Id == i.TripId);
                    var date = trip.StartDate!.Value.AddDays(i.Index);
                    if (date < fromDate || date > lastDate) continue;
                    var start = date.ToDateTime(i.Time!.Value);
                    items.Add(new CalendarItem(CalendarItemKind.Itinerary, new EntityRef(EntityKind.Trip, i.TripId), i.Title, start, start.AddHours(1), false, trip.Accent, "MapPin",
                        Subtitle: trip.Name, IsDone: i.IsDone, Location: i.Place));
                }
            }
        }

        if (sources.HasFlag(CalendarSources.Finance))
        {
            // due dates of bills, subscriptions and income, expanded from their rule (never copied into events).
            // Only names are shown: amounts stay inside Finance, where privacy mode applies.
            var bills = await db.RecurringItems.AsNoTracking().Include(r => r.Occurrences)
                .Where(r => r.IsActive && r.ShowOnCalendar && r.StartDate <= lastDate && (r.EndDate == null || r.EndDate >= fromDate))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var bill in bills)
            {
                var status = bill.Occurrences.ToDictionary(o => o.DueDate, o => o.Status);
                foreach (var date in Recurrence.Dates(Arcadia.Core.Finance.RecurringMath.RuleOf(bill), fromDate, lastDate))
                {
                    if (status.TryGetValue(date, out var s) && s == Arcadia.Core.Finance.OccurrenceStatus.Skipped) continue;
                    var start = date.ToDateTime(TimeOnly.MinValue);
                    items.Add(new CalendarItem(CalendarItemKind.Bill, new EntityRef(EntityKind.Recurring, bill.Id), bill.Name, start, start.AddDays(1), true,
                        bill.Kind == Arcadia.Core.Finance.RecurringKind.Income ? "Green" : "Amber", bill.Kind == Arcadia.Core.Finance.RecurringKind.Income ? "Income" : "Receipt",
                        OccurrenceDate: date, IsRecurring: true, IsDone: status.ContainsKey(date)));
                }
            }
        }

        items.Sort(Compare);
        return items;
    }

    public async Task<IReadOnlyList<CalendarItem>> SearchEventsAsync(string text, DateTime? from = null, DateTime? to = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pattern = "%" + text.Trim().Replace("%", "").Replace("_", "") + "%";
        var events = await db.CalendarEvents.AsNoTracking().Include(e => e.Category).Include(e => e.Exceptions)
            .Where(e => EF.Functions.Like(e.Title, pattern) || EF.Functions.Like(e.Description ?? "", pattern) || EF.Functions.Like(e.Location ?? "", pattern)
                        || EF.Functions.Like(e.Tags, pattern) || (e.Category != null && (EF.Functions.Like(e.Category.Name ?? "", pattern) || EF.Functions.Like(e.Category.Key ?? "", pattern))))
            .Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetLocalNow().DateTime;
        var list = new List<CalendarItem>();
        foreach (var ev in events)
        {
            // one result per event: its first occurrence in the range; without a range, the next one (or the series start)
            var occ = from != null || to != null
                ? Recurrence.Expand(ev, from ?? ev.Start, to ?? (from ?? ev.Start).AddYears(5)).FirstOrDefault()
                : Recurrence.Expand(ev, now, now.AddYears(2)).FirstOrDefault()
                  ?? new Occurrence(ev, DateOnly.FromDateTime(ev.Start), ev.Start, ev.Start + Recurrence.Duration(ev), ev.Title, ev.Location, ev.Description, false);
            if (occ != null) list.Add(FromOccurrence(occ));
        }
        return list.OrderBy(i => i.Start).ToList();
    }

    internal static bool IsGaming(string? categoryKey, EntityKind? context) =>
        context == EntityKind.Game || string.Equals(categoryKey, "gaming", StringComparison.OrdinalIgnoreCase);

    private static CalendarItem FromOccurrence(Occurrence o)
    {
        var ev = o.Event;
        return new CalendarItem(CalendarItemKind.Event, new EntityRef(EntityKind.Event, ev.Id), o.Title, o.Start, o.End, ev.IsAllDay,
            ev.Category?.Accent ?? "Blue", ev.Category?.Icon ?? "Calendar", OccurrenceDate: o.OriginalDate, IsRecurring: ev.IsRecurring,
            ReminderMinutes: ev.ReminderMinutes, Location: o.Location, Context: ev.Context);
    }

    /// <summary>All-day items first (longest first), then by start time, then by title.</summary>
    private static int Compare(CalendarItem a, CalendarItem b)
    {
        if (a.IsAllDay != b.IsAllDay) return a.IsAllDay ? -1 : 1;
        var c = a.Start.CompareTo(b.Start);
        if (c != 0) return c;
        if (a.IsAllDay) { c = (b.End - b.Start).CompareTo(a.End - a.Start); if (c != 0) return c; }
        return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }
}
