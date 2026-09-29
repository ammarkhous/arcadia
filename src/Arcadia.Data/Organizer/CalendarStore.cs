using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Organizer;

/// <summary>
/// Calendar events and categories. Recurring events are one row plus exceptions; see <see cref="Recurrence"/>.
/// Editing scopes: "this event" writes an exception; "this and future" splits the series (<see cref="Recurrence.SplitAt"/>);
/// "all events" edits the series and moves every occurrence by the same number of days the user moved the one they opened.
/// </summary>
public sealed class CalendarStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : ICalendarStore
{
    public const int MaxReminderMinutes = 60 * 24 * 28;
    public const int MaxInterval = 999;
    /// <summary>The longest single event considered when looking for events overlapping a range.</summary>
    private static readonly TimeSpan LongestEvent = TimeSpan.FromDays(400);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ───────────────────────── categories ─────────────────────────

    public async Task<IReadOnlyList<CalendarCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.CalendarCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalendarCategory> SaveCategoryAsync(int? id, string name, string icon, string accent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("A category needs a name (80 characters at most).");
        if (!Palette.Accents.Contains(accent)) throw new ArgumentException("Pick one of Arcadia's accent tones.");
        if (string.IsNullOrWhiteSpace(icon) || icon.Length > 32) throw new ArgumentException("Pick an icon.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CalendarCategory category;
        if (id is { } existing)
            category = await db.CalendarCategories.FindAsync([existing], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The category no longer exists.");
        else
        {
            category = new CalendarCategory { SortOrder = (await db.CalendarCategories.MaxAsync(c => (int?)c.SortOrder, cancellationToken).ConfigureAwait(false) ?? 0) + 1 };
            db.CalendarCategories.Add(category);
        }
        category.Name = name.Trim();
        category.Icon = icon;
        category.Accent = accent;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return category;
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.CalendarEvents.Where(e => e.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.CategoryId, (int?)null), cancellationToken).ConfigureAwait(false);
        await db.CalendarCategories.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── events ─────────────────────────

    public async Task<CalendarEvent?> GetEventAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.CalendarEvents.AsNoTracking().Include(e => e.Category).Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalendarEvent> CreateEventAsync(EventDraft draft, CancellationToken cancellationToken = default)
    {
        var d = Normalize(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = Now;
        var ev = new CalendarEvent { CreatedAtUtc = now, UpdatedAtUtc = now };
        Apply(ev, d);
        await CheckLinksAsync(db, ev, cancellationToken).ConfigureAwait(false);
        db.CalendarEvents.Add(ev);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexAsync(db, ev, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ev;
    }

    public async Task<int> UpdateEventAsync(int id, EventDraft draft, RecurrenceScope scope = RecurrenceScope.AllEvents, DateOnly? occurrenceDate = null, CancellationToken cancellationToken = default)
    {
        var d = Normalize(draft);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ev = await db.CalendarEvents.Include(e => e.Exceptions).FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false)
                 ?? throw new KeyNotFoundException("The event no longer exists.");
        var now = Now;
        var resultId = ev.Id;

        if (!ev.IsRecurring || occurrenceDate is not { } occ)
        {
            Apply(ev, d);
        }
        else if (scope == RecurrenceScope.ThisEvent)
        {
            if (!Recurrence.IsRuleDate(ev, occ)) throw new ArgumentException("That occurrence does not exist.");
            var x = ev.Exceptions.FirstOrDefault(e => e.OriginalDate == occ);
            if (x == null) ev.Exceptions.Add(x = new CalendarEventException { OriginalDate = occ });
            x.IsCancelled = false;
            x.Start = d.Start;
            x.End = d.End;
            x.Title = d.Title == ev.Title ? null : d.Title;
            x.Location = d.Location == ev.Location ? null : d.Location;
            x.Description = d.Description == ev.Description ? null : d.Description;
        }
        else if (scope == RecurrenceScope.ThisAndFuture && Recurrence.SplitAt(ev, occ) is { } tail)
        {
            // the tail keeps its moved/cancelled occurrences only if the rule's timing did not change
            var shifted = ShiftFrom(occ, d);
            Apply(tail, d);
            if (shifted != 0 || tail.Frequency != ev.Frequency) tail.Exceptions.Clear();
            tail.CreatedAtUtc = now;
            tail.UpdatedAtUtc = now;
            db.CalendarEvents.Add(tail);
            if (ev.RecursUntil < DateOnly.FromDateTime(ev.Start)) db.CalendarEvents.Remove(ev);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            resultId = tail.Id;
            await IndexAsync(db, tail, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // all events (or "this and future" on the first occurrence): keep the series' first date, moved by the same days
            var shift = ShiftFrom(occ, d);
            var first = DateOnly.FromDateTime(ev.Start).AddDays(shift);
            var length = d.End - d.Start;
            var series = d with { Start = first.ToDateTime(TimeOnly.FromDateTime(d.Start)), End = first.ToDateTime(TimeOnly.FromDateTime(d.Start)) + length };
            Apply(ev, series);
            if (shift != 0) ev.Exceptions.Clear();
        }

        var removed = db.Entry(ev).State == EntityState.Deleted;
        if (!removed)
        {
            await CheckLinksAsync(db, ev, cancellationToken).ConfigureAwait(false);
            ev.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (removed) await SearchIndex.DeleteAsync(db, SearchResultKind.Event, id, cancellationToken).ConfigureAwait(false);
        else await IndexAsync(db, ev, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resultId;
    }

    /// <summary>Days between the occurrence the user opened and the date they gave it in the editor.</summary>
    private static int ShiftFrom(DateOnly occurrence, EventDraft d) => DateOnly.FromDateTime(d.Start).DayNumber - occurrence.DayNumber;

    public async Task DeleteEventAsync(int id, RecurrenceScope scope = RecurrenceScope.AllEvents, DateOnly? occurrenceDate = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ev = await db.CalendarEvents.Include(e => e.Exceptions).FirstOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        if (ev == null) return;
        var first = DateOnly.FromDateTime(ev.Start);
        var removeAll = !ev.IsRecurring || occurrenceDate is not { } occ || scope == RecurrenceScope.AllEvents
                        || (scope == RecurrenceScope.ThisAndFuture && occ <= first);
        if (removeAll)
        {
            db.CalendarEvents.Remove(ev);
            await SearchIndex.DeleteAsync(db, SearchResultKind.Event, id, cancellationToken).ConfigureAwait(false);
        }
        else if (scope == RecurrenceScope.ThisEvent)
        {
            var x = ev.Exceptions.FirstOrDefault(e => e.OriginalDate == occurrenceDate!.Value);
            if (x == null) ev.Exceptions.Add(x = new CalendarEventException { OriginalDate = occurrenceDate!.Value });
            x.IsCancelled = true;
            ev.UpdatedAtUtc = Now;
        }
        else
        {
            ev.RecursUntil = occurrenceDate!.Value.AddDays(-1);
            foreach (var x in ev.Exceptions.Where(x => x.OriginalDate >= occurrenceDate.Value).ToList()) ev.Exceptions.Remove(x);
            ev.UpdatedAtUtc = Now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsForRangeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await EventsForRange(db, from, to).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static IQueryable<CalendarEvent> EventsForRange(ArcadiaDbContext db, DateTime from, DateTime to)
    {
        // one-off: starts before the range ends and ends on/after its first day (all-day End is the last day at 00:00);
        // series: first occurrence before the range ends and not finished long before it (occurrences can last up to 400 days)
        var fromDay = from.Date;
        var seriesUntil = DateOnly.FromDateTime(from).AddDays(-(int)LongestEvent.TotalDays);
        return db.CalendarEvents.AsNoTracking().Include(e => e.Category).Include(e => e.Exceptions)
            .Where(e => e.Start < to
                        && ((e.Frequency == RecurrenceFrequency.None && e.End >= fromDay)
                            || (e.Frequency != RecurrenceFrequency.None && (e.RecursUntil == null || e.RecursUntil >= seriesUntil))));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static EventDraft Normalize(EventDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Title)) throw new ArgumentException("An event needs a title.");
        if (d.Title.Trim().Length > 300) throw new ArgumentException("The title is too long (300 characters at most).");
        if (d.Location?.Length > 300) throw new ArgumentException("The location is too long.");
        if (d.Description?.Length > 8000 || d.Notes?.Length > 8000) throw new ArgumentException("The text is too long (8000 characters at most).");
        if (d.ReminderMinutes is < 0 or > MaxReminderMinutes) throw new ArgumentException("A reminder can be at most four weeks before.");
        if (d.Interval is < 1 or > MaxInterval) throw new ArgumentException("Repeat every 1 to 999.");
        var start = d.IsAllDay ? d.Start.Date : TrimSeconds(d.Start);
        var end = d.IsAllDay ? d.End.Date : TrimSeconds(d.End);
        if (end < start) throw new ArgumentException("The event cannot end before it starts.");
        if (end - start > LongestEvent) throw new ArgumentException("An event can last at most 400 days.");
        if (d.RecursUntil is { } until && until < DateOnly.FromDateTime(start)) throw new ArgumentException("The repetition cannot end before the event starts.");
        var weekdays = d.Frequency == RecurrenceFrequency.Weekly ? d.Weekdays : WeekdayMask.None;
        return d with
        {
            Title = d.Title.Trim(), Start = start, End = end, Location = Blank(d.Location), Description = Blank(d.Description), Notes = Blank(d.Notes),
            Tags = TagText.Normalize(d.Tags), Weekdays = weekdays, Interval = d.Frequency == RecurrenceFrequency.None ? 1 : d.Interval,
            RecursUntil = d.Frequency == RecurrenceFrequency.None ? null : d.RecursUntil,
        };
    }

    private static DateTime TrimSeconds(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);

    private static void Apply(CalendarEvent e, EventDraft d)
    {
        e.Title = d.Title;
        e.Description = d.Description;
        e.Location = d.Location;
        e.PlaceId = d.PlaceId;
        e.CategoryId = d.CategoryId;
        e.IsAllDay = d.IsAllDay;
        e.Start = DateTime.SpecifyKind(d.Start, DateTimeKind.Unspecified);
        e.End = DateTime.SpecifyKind(d.End, DateTimeKind.Unspecified);
        e.ReminderMinutes = d.ReminderMinutes;
        e.Frequency = d.Frequency;
        e.Interval = d.Interval;
        e.Weekdays = d.Weekdays;
        e.RecursUntil = d.RecursUntil;
        e.Notes = d.Notes;
        e.Tags = d.Tags;
        e.ContextKind = d.Context?.Kind;
        e.ContextId = d.Context?.Id;
    }

    private static async Task CheckLinksAsync(ArcadiaDbContext db, CalendarEvent e, CancellationToken ct)
    {
        if (e.CategoryId is { } c && !await db.CalendarCategories.AnyAsync(x => x.Id == c, ct).ConfigureAwait(false)) e.CategoryId = null;
        if (e.PlaceId is { } p && !await db.TravelPlaces.AnyAsync(x => x.Id == p, ct).ConfigureAwait(false)) e.PlaceId = null;
    }

    private static async Task IndexAsync(ArcadiaDbContext db, CalendarEvent e, CancellationToken ct)
    {
        var category = e.CategoryId is { } c ? await db.CalendarCategories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c, ct).ConfigureAwait(false) : null;
        await SearchIndex.IndexEventAsync(db, e, category?.Name ?? category?.Key, ct).ConfigureAwait(false);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
