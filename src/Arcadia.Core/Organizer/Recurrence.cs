namespace Arcadia.Core.Organizer;

/// <summary>A repetition rule: from <see cref="First"/>, every <see cref="Interval"/> days/weeks/months/years, optionally on weekdays, until a last date.</summary>
public readonly record struct RecurrenceRule(DateOnly First, RecurrenceFrequency Frequency, int Interval = 1, WeekdayMask Weekdays = WeekdayMask.None, DateOnly? Until = null);

/// <summary>One concrete appearance of an event in the calendar.</summary>
/// <param name="OriginalDate">The date the rule produced (identifies the occurrence even after it was moved).</param>
public sealed record Occurrence(CalendarEvent Event, DateOnly OriginalDate, DateTime Start, DateTime End, string Title, string? Location, string? Description, bool IsException)
{
    public bool IsAllDay => Event.IsAllDay;
}

/// <summary>
/// Expands an event's recurrence rule into occurrences inside a range. Nothing is materialised in the database; the
/// expansion jumps close to the range start, so a daily event from 2010 costs the same as one from last week.
/// Rules: daily/weekly/monthly/yearly with an interval; weekly on selected weekdays (weeks start Monday, counted from the
/// week of the first occurrence); monthly on the start's day, clamped to the month's last day (the 31st becomes the 30th or
/// 28th/29th); yearly on the same date (29 February becomes 28 February in common years); optional last date.
/// </summary>
public static class Recurrence
{
    /// <summary>Safety net against pathological ranges; far more than any view shows.</summary>
    public const int MaxOccurrences = 5000;

    public static IReadOnlyList<Occurrence> Expand(CalendarEvent ev, DateTime rangeStart, DateTime rangeEnd)
    {
        var list = new List<Occurrence>();
        if (rangeEnd <= rangeStart) return list;
        var exceptions = ev.Exceptions.ToDictionary(x => x.OriginalDate);
        var duration = Duration(ev);

        foreach (var date in Dates(ev, DateOnly.FromDateTime(rangeStart - duration).AddDays(-1), DateOnly.FromDateTime(rangeEnd)))
        {
            var start = date.ToDateTime(TimeOnly.FromDateTime(ev.Start));
            Occurrence occ;
            if (exceptions.TryGetValue(date, out var x))
            {
                if (x.IsCancelled) continue;
                var s = x.Start ?? start;
                occ = new Occurrence(ev, date, s, x.End ?? s + duration, x.Title ?? ev.Title, x.Location ?? ev.Location, x.Description ?? ev.Description, true);
            }
            else occ = new Occurrence(ev, date, start, start + duration, ev.Title, ev.Location, ev.Description, false);

            if (Overlaps(occ, rangeStart, rangeEnd)) list.Add(occ);
            if (list.Count >= MaxOccurrences) break;
        }

        // an occurrence moved into this range from a date outside the scanned window
        foreach (var x in ev.Exceptions.Where(x => !x.IsCancelled && x.Start != null && list.All(o => o.OriginalDate != x.OriginalDate)))
        {
            if (!IsRuleDate(ev, x.OriginalDate)) continue;
            var occ = new Occurrence(ev, x.OriginalDate, x.Start!.Value, x.End ?? x.Start.Value + duration, x.Title ?? ev.Title, x.Location ?? ev.Location, x.Description ?? ev.Description, true);
            if (Overlaps(occ, rangeStart, rangeEnd)) list.Add(occ);
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    /// <summary>All-day events span whole days (End is the inclusive last day); timed events their exact length.</summary>
    public static TimeSpan Duration(CalendarEvent ev) =>
        ev.IsAllDay ? TimeSpan.FromDays(Math.Max(1, (ev.End.Date - ev.Start.Date).Days + 1)) : ev.End > ev.Start ? ev.End - ev.Start : TimeSpan.Zero;

    private static bool Overlaps(Occurrence o, DateTime from, DateTime to) =>
        o.End > o.Start ? o.Start < to && o.End > from : o.Start >= from && o.Start < to;

    /// <summary>True when the rule produces an occurrence on this date.</summary>
    public static bool IsRuleDate(CalendarEvent ev, DateOnly date) => Dates(ev, date, date).Any();

    /// <summary>Dates (in order) on which occurrences start, between <paramref name="from"/> and <paramref name="to"/> inclusive.</summary>
    public static IEnumerable<DateOnly> Dates(CalendarEvent ev, DateOnly from, DateOnly to) => Dates(RuleOf(ev), from, to);

    public static RecurrenceRule RuleOf(CalendarEvent ev) => new(DateOnly.FromDateTime(ev.Start), ev.Frequency, ev.Interval, ev.Weekdays, ev.RecursUntil);

    /// <summary>
    /// The same rules for anything that repeats by date (Phase 7 bills, subscriptions and income use this too): dates on
    /// which the rule falls between <paramref name="from"/> and <paramref name="to"/> inclusive.
    /// </summary>
    public static IEnumerable<DateOnly> Dates(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        var first = rule.First;
        if (rule.Until is { } until && until < to) to = until;
        if (from < first) from = first;
        if (to < from) yield break;
        var interval = Math.Max(1, rule.Interval);

        switch (rule.Frequency)
        {
            case RecurrenceFrequency.None:
                if (first >= from && first <= to) yield return first;
                break;

            case RecurrenceFrequency.Daily:
            {
                var skip = (from.DayNumber - first.DayNumber + interval - 1) / interval;
                for (var d = first.AddDays(skip * interval); d <= to; d = d.AddDays(interval)) yield return d;
                break;
            }

            case RecurrenceFrequency.Weekly:
            {
                var mask = rule.Weekdays == WeekdayMask.None ? MaskOf(first.DayOfWeek) : rule.Weekdays;
                var week0 = MondayOf(first);
                var weeks = Math.Max(0, (MondayOf(from).DayNumber - week0.DayNumber) / 7);
                var week = week0.AddDays((weeks / interval) * interval * 7);
                for (; week <= to; week = week.AddDays(7 * interval))
                    for (var i = 0; i < 7; i++)
                    {
                        var d = week.AddDays(i);
                        if (d < from || d > to || (MaskOf(d.DayOfWeek) & mask) == 0) continue;
                        yield return d;
                    }
                break;
            }

            case RecurrenceFrequency.Monthly:
            {
                var months = Math.Max(0, (from.Year - first.Year) * 12 + from.Month - first.Month - 1);
                for (var n = (months / interval) * interval; ; n += interval)
                {
                    var m = new DateOnly(first.Year, first.Month, 1).AddMonths(n);
                    if (m > to) break;
                    var d = new DateOnly(m.Year, m.Month, Math.Min(first.Day, DateTime.DaysInMonth(m.Year, m.Month)));
                    if (d >= from && d <= to) yield return d;
                }
                break;
            }

            case RecurrenceFrequency.Yearly:
            {
                var years = Math.Max(0, from.Year - first.Year - 1);
                for (var n = (years / interval) * interval; ; n += interval)
                {
                    var y = first.Year + n;
                    if (y > to.Year || y > 9998) break;
                    var d = new DateOnly(y, first.Month, Math.Min(first.Day, DateTime.DaysInMonth(y, first.Month)));
                    if (d >= from && d <= to) yield return d;
                }
                break;
            }
        }
    }

    public static WeekdayMask MaskOf(DayOfWeek day) => (WeekdayMask)(1 << (int)day);

    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>
    /// "This and future events": the series before <paramref name="occurrenceDate"/> keeps its identity and now ends the day
    /// before; the returned copy starts at that occurrence (same rule, same end) and receives the user's changes. Exceptions
    /// are divided between the two by date. Returns null when the occurrence is the first one (edit the whole series instead).
    /// </summary>
    public static CalendarEvent? SplitAt(CalendarEvent series, DateOnly occurrenceDate)
    {
        if (!series.IsRecurring || occurrenceDate <= DateOnly.FromDateTime(series.Start)) return null;
        var offset = occurrenceDate.DayNumber - DateOnly.FromDateTime(series.Start).DayNumber;
        var tail = new CalendarEvent
        {
            Title = series.Title, Description = series.Description, Location = series.Location, PlaceId = series.PlaceId, CategoryId = series.CategoryId,
            IsAllDay = series.IsAllDay, Start = series.Start.AddDays(offset), End = series.End.AddDays(offset), ReminderMinutes = series.ReminderMinutes,
            Frequency = series.Frequency, Interval = series.Interval, Weekdays = series.Weekdays, RecursUntil = series.RecursUntil,
            Notes = series.Notes, Tags = series.Tags, ContextKind = series.ContextKind, ContextId = series.ContextId,
            CreatedAtUtc = series.CreatedAtUtc, UpdatedAtUtc = series.UpdatedAtUtc,
        };
        foreach (var x in series.Exceptions.Where(x => x.OriginalDate >= occurrenceDate).ToList())
        {
            series.Exceptions.Remove(x);
            tail.Exceptions.Add(new CalendarEventException
            {
                OriginalDate = x.OriginalDate, IsCancelled = x.IsCancelled, Start = x.Start, End = x.End, Title = x.Title, Location = x.Location, Description = x.Description,
            });
        }
        series.RecursUntil = occurrenceDate.AddDays(-1);
        return tail;
    }
}
