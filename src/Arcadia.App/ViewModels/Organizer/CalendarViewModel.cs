using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Organizer;

/// <summary>
/// The calendar: Today (the daily agenda), Month, Week, Day and Agenda over one projection of everything dated in
/// Arcadia (<see cref="ICalendarQueryService"/>). Only the visible range is loaded. Sources can be switched off
/// without touching data; the last view and the sources are remembered.
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject, IActivatable, IShortcutTarget, IRecipient<OrganizerChangedMessage>
{
    private readonly ICalendarStore _store;
    private readonly ICalendarQueryService _query;
    private readonly ITaskStore _tasks;
    private readonly INoteQueryService _notes;
    private CancellationTokenSource? _cts;
    private bool _loaded;
    private IReadOnlyList<CalendarCategory> _categories = [];

    public CalendarViewModel(ICalendarStore store, ICalendarQueryService query, ITaskStore tasks, INoteQueryService notes, OrganizerContext ctx)
    {
        _store = store;
        _query = query;
        _tasks = tasks;
        _notes = notes;
        Ctx = ctx;
        var loc = ctx.Loc;
        Views =
        [
            new(CalendarView.Today, loc["CalView_Today"], "Home", "T"),
            new(CalendarView.Month, loc["CalView_Month"], "ViewGrid", "M"),
            new(CalendarView.Week, loc["CalView_Week"], "SplitView", "W"),
            new(CalendarView.Day, loc["CalView_Day"], "Clock", "D"),
            new(CalendarView.Agenda, loc["CalView_Agenda"], "Agenda", "A"),
        ];
        _view = Enum.TryParse<CalendarView>(ctx.Prefs.CalendarView, true, out var v) ? v : CalendarView.Today;
        foreach (var o in Views) o.IsSelected = o.View == _view;
        var p = ctx.Prefs;
        Sources =
        [
            new(CalendarSources.Events, loc["CalSource_Events"], "Calendar", "Blue", p.ShowEvents),
            new(CalendarSources.Tasks, loc["CalSource_Tasks"], "Tasks", "Purple", p.ShowTasks),
            new(CalendarSources.Projects, loc["CalSource_Projects"], "Flag", "Cyan", p.ShowProjects),
            new(CalendarSources.Travel, loc["CalSource_Travel"], "Suitcase", "Green", p.ShowTravel),
            new(CalendarSources.Gaming, loc["CalSource_Gaming"], "Games", "Purple", p.ShowGaming),
            new(CalendarSources.Finance, loc["CalSource_Finance"], "Receipt", "Amber", p.ShowFinance),
        ];
        foreach (var s in Sources) s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SourceToggle.IsOn)) OnSourcesChanged(); };
        _anchor = ctx.Today;
        _selectedDay = ctx.Today;
        HourLabels = Enumerable.Range(0, 24).Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString(loc.Culture.DateTimeFormat.ShortTimePattern.Contains('t') ? "h tt" : "HH:mm", loc.Culture)).ToList();
        ctx.Messenger.Register(this);
    }

    public OrganizerContext Ctx { get; }
    public IReadOnlyList<CalendarViewOption> Views { get; }
    public IReadOnlyList<SourceToggle> Sources { get; }
    public IReadOnlyList<string> HourLabels { get; }
    public ObservableCollection<string> WeekdayHeaders { get; } = [];
    public ObservableCollection<MonthDay> MonthDays { get; } = [];
    public ObservableCollection<DayColumn> Columns { get; } = [];
    public ObservableCollection<AgendaGroup> Agenda { get; } = [];
    /// <summary>The agenda as one keyboard list: each day heading followed by its entries.</summary>
    public ObservableCollection<object> AgendaRows { get; } = [];
    public ObservableCollection<CalendarEntry> SelectedDayEntries { get; } = [];
    public ObservableCollection<CategoryRow> Categories { get; } = [];
    public TodayState Today { get; } = new();

    [ObservableProperty] private CalendarView _view;
    [ObservableProperty] private DateOnly _anchor;
    [ObservableProperty] private DateOnly _selectedDay;
    [ObservableProperty] private string _rangeTitle = string.Empty;
    [ObservableProperty] private string _rangeSubtitle = string.Empty;
    [ObservableProperty] private string _selectedDayTitle = string.Empty;
    [ObservableProperty] private CalendarEntry? _selectedEntry;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _dayStartHour = 7;
    [ObservableProperty] private int _dayEndHour = 22;

    private CalendarSources ActiveSources => Sources.Where(s => s.IsOn).Aggregate(CalendarSources.None, (a, s) => a | s.Source);

    public async Task ActivateAsync()
    {
        await Ctx.Ready;
        DayStartHour = Math.Clamp(Ctx.Prefs.DayStartHour, 0, 22);
        DayEndHour = Math.Clamp(Ctx.Prefs.DayEndHour, DayStartHour + 1, 24);
        if (!_loaded) { _loaded = true; await LoadCategoriesAsync(); }
        await LoadAsync();
    }

    public void Receive(OrganizerChangedMessage message)
    {
        if (_loaded) _ = LoadAsync();
    }

    private bool _quietView;

    partial void OnViewChanged(CalendarView value)
    {
        foreach (var o in Views) o.IsSelected = o.View == value;
        Ctx.Prefs.CalendarView = value.ToString();
        Ctx.SavePrefs();
        if (_quietView) return;
        if (value == CalendarView.Today) Anchor = Ctx.Today;
        _ = LoadAsync();
    }

    /// <summary>Switches the view without reloading (the caller loads once afterwards).</summary>
    private void SetViewQuietly(CalendarView view)
    {
        _quietView = true;
        try { View = view; } finally { _quietView = false; }
    }

    private void OnSourcesChanged()
    {
        var p = Ctx.Prefs;
        p.ShowEvents = Sources[0].IsOn;
        p.ShowTasks = Sources[1].IsOn;
        p.ShowProjects = Sources[2].IsOn;
        p.ShowTravel = Sources[3].IsOn;
        p.ShowGaming = Sources[4].IsOn;
        p.ShowFinance = Sources[5].IsOn;
        Ctx.SavePrefs();
        _ = LoadAsync();
    }

    // ───────────── navigation ─────────────

    [RelayCommand] private void SetView(CalendarViewOption? o) { if (o != null) View = o.View; }

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(1);

    private void Step(int dir)
    {
        Anchor = View switch
        {
            CalendarView.Month => Anchor.AddMonths(dir),
            CalendarView.Week => Anchor.AddDays(7 * dir),
            CalendarView.Agenda => Anchor.AddDays(14 * dir),
            _ => Anchor.AddDays(dir),
        };
        if (View is CalendarView.Today && Anchor != Ctx.Today) SetViewQuietly(CalendarView.Day);
        SelectedDay = View == CalendarView.Month ? new DateOnly(Anchor.Year, Anchor.Month, 1) : Anchor;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void GoToday()
    {
        Anchor = Ctx.Today;
        SelectedDay = Ctx.Today;
        _ = LoadAsync();
    }

    /// <summary>Opens a date (from Home, search or the palette) in the given view.</summary>
    public async Task ShowDateAsync(DateOnly date, CalendarView? view = null)
    {
        _loaded = true;
        if (view is { } v && v != View) SetViewQuietly(v);
        Anchor = date;
        SelectedDay = date;
        await LoadAsync();
    }

    [RelayCommand]
    private void SelectDay(MonthDay? day)
    {
        if (day == null) return;
        SelectedDay = day.Date;
        foreach (var d in MonthDays) d.IsSelected = d.Date == day.Date;
        FillSelectedDay();
    }

    [RelayCommand]
    private async Task OpenDayAsync(object? day)
    {
        var date = day switch { MonthDay m => m.Date, DayColumn c => c.Date, DateOnly d => d, _ => SelectedDay };
        await ShowDateAsync(date, CalendarView.Day);
    }

    // ───────────── loading ─────────────

    private (DateTime From, DateTime To) Range()
    {
        switch (View)
        {
            case CalendarView.Month:
            {
                var first = new DateOnly(Anchor.Year, Anchor.Month, 1);
                var start = first.AddDays(-(((int)first.DayOfWeek - (int)FirstDay + 7) % 7));
                return (start.ToDateTime(TimeOnly.MinValue), start.AddDays(42).ToDateTime(TimeOnly.MinValue));
            }
            case CalendarView.Week:
            {
                var start = Anchor.AddDays(-(((int)Anchor.DayOfWeek - (int)FirstDay + 7) % 7));
                return (start.ToDateTime(TimeOnly.MinValue), start.AddDays(7).ToDateTime(TimeOnly.MinValue));
            }
            case CalendarView.Agenda:
                return (Anchor.ToDateTime(TimeOnly.MinValue), Anchor.AddDays(42).ToDateTime(TimeOnly.MinValue));
            default:
                return (Anchor.ToDateTime(TimeOnly.MinValue), Anchor.AddDays(1).ToDateTime(TimeOnly.MinValue));
        }
    }

    private DayOfWeek FirstDay => Ctx.Loc.Culture.DateTimeFormat.FirstDayOfWeek;

    public async Task LoadAsync()
    {
        await Ctx.Ready;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var (from, to) = Range();
            var items = await _query.GetItemsAsync(from, to, ActiveSources, cts.Token);
            var chips = await ContextChip.ResolveAsync(Ctx, items.Select(i => i.Context).OfType<EntityRef>());
            if (cts.IsCancellationRequested) return;
            var entries = items.Select(i => new CalendarEntry(i, Ctx.Loc, i.Context is { } c ? chips.GetValueOrDefault(c) : null)).ToList();
            UpdateTitles(from, to);
            switch (View)
            {
                case CalendarView.Month: BuildMonth(from, entries); break;
                case CalendarView.Week: BuildColumns(DateOnly.FromDateTime(from), 7, entries); break;
                case CalendarView.Day: BuildColumns(Anchor, 1, entries); break;
                case CalendarView.Agenda: BuildAgenda(entries); break;
                default: await BuildTodayAsync(entries, cts.Token); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Ctx.Logger.LogError(ex, "Loading the calendar failed");
            Ctx.Notifications.Show(Ctx.Loc["Cal_ErrLoad"], NotificationKind.Warning);
        }
        finally
        {
            if (ReferenceEquals(cts, _cts)) IsBusy = false;
        }
    }

    private void UpdateTitles(DateTime from, DateTime to)
    {
        var c = Ctx.Loc.Culture;
        var loc = Ctx.Loc;
        switch (View)
        {
            case CalendarView.Month:
                RangeTitle = Anchor.ToString("MMMM yyyy", c);
                RangeSubtitle = string.Empty;
                break;
            case CalendarView.Week:
                var last = DateOnly.FromDateTime(to).AddDays(-1);
                RangeTitle = loc.Format("Cal_WeekTitle", ISOWeek.GetWeekOfYear(from));
                RangeSubtitle = $"{DateOnly.FromDateTime(from).ToString("d MMM", c)} – {last.ToString("d MMM yyyy", c)}";
                break;
            case CalendarView.Agenda:
                RangeTitle = loc["CalView_Agenda"];
                RangeSubtitle = $"{Anchor.ToString("d MMM", c)} – {DateOnly.FromDateTime(to).AddDays(-1).ToString("d MMM yyyy", c)}";
                break;
            case CalendarView.Today:
                // the page itself shows the big date
                RangeTitle = loc["CalView_Today"];
                RangeSubtitle = string.Empty;
                break;
            default:
                RangeTitle = Anchor.ToString("dddd", c);
                RangeSubtitle = Anchor.ToString("D", c);
                break;
        }
    }

    private void BuildMonth(DateTime from, List<CalendarEntry> entries)
    {
        var c = Ctx.Loc.Culture;
        WeekdayHeaders.Clear();
        for (var i = 0; i < 7; i++) WeekdayHeaders.Add(c.DateTimeFormat.AbbreviatedDayNames[((int)FirstDay + i) % 7]);
        MonthDays.Clear();
        var start = DateOnly.FromDateTime(from);
        var today = Ctx.Today;
        for (var i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            var day = new MonthDay(d, d.Month == Anchor.Month, d == today, d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { IsSelected = d == SelectedDay };
            var onDay = entries.Where(e => e.Item.StartDate <= d && e.Item.EndDate >= d).ToList();
            foreach (var e in onDay.Take(MonthDay.VisibleEntries)) day.Entries.Add(e);
            day.MoreText = onDay.Count > MonthDay.VisibleEntries ? Ctx.Loc.Format("Cal_More", onDay.Count - MonthDay.VisibleEntries) : null;
            day.HasTasks = onDay.Any(e => e.IsTask && !e.IsDone);
            MonthDays.Add(day);
        }
        _monthEntries = entries;
        if (SelectedDay < start || SelectedDay >= start.AddDays(42) || SelectedDay.Month != Anchor.Month) SelectedDay = Anchor.Month == today.Month && Anchor.Year == today.Year ? today : new DateOnly(Anchor.Year, Anchor.Month, 1);
        foreach (var d in MonthDays) d.IsSelected = d.Date == SelectedDay;
        FillSelectedDay();
    }

    private List<CalendarEntry> _monthEntries = [];

    private void FillSelectedDay()
    {
        SelectedDayEntries.Clear();
        foreach (var e in _monthEntries.Where(e => e.Item.StartDate <= SelectedDay && e.Item.EndDate >= SelectedDay)) SelectedDayEntries.Add(e);
        SelectedDayTitle = OrgText.Day(SelectedDay, Ctx.Today, Ctx.Loc) + " · " + SelectedDay.ToString("D", Ctx.Loc.Culture);
    }

    private void BuildColumns(DateOnly start, int days, List<CalendarEntry> entries)
    {
        var c = Ctx.Loc.Culture;
        Columns.Clear();
        var now = Ctx.Now;
        for (var i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            var col = new DayColumn(d, d.ToString("ddd", c), d.Day.ToString(c), d == Ctx.Today, d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
            var dayStart = d.ToDateTime(TimeOnly.MinValue);
            var dayEnd = dayStart.AddDays(1);
            foreach (var e in entries.Where(e => e.IsAllDay && e.Item.StartDate <= d && e.Item.EndDate >= d)) col.AllDay.Add(e);
            var timed = entries.Where(e => !e.IsAllDay && e.Item.Start < dayEnd && (e.Item.End > dayStart || e.Item.Start >= dayStart))
                .Select(e =>
                {
                    var s = e.Item.Start < dayStart ? dayStart : e.Item.Start;
                    var en = e.Item.End > dayEnd ? dayEnd : e.Item.End <= e.Item.Start ? s.AddMinutes(30) : e.Item.End;
                    return (e, (s - dayStart).TotalMinutes, Math.Max(15, (en - s).TotalMinutes));
                });
            foreach (var b in TimedBlock.Layout(timed)) col.Timed.Add(b);
            if (d == Ctx.Today) col.NowMinutes = (now - dayStart).TotalMinutes;
            Columns.Add(col);
        }
    }

    private void BuildAgenda(List<CalendarEntry> entries)
    {
        Agenda.Clear();
        var c = Ctx.Loc.Culture;
        var today = Ctx.Today;
        var (from, to) = Range();
        for (var d = DateOnly.FromDateTime(from); d < DateOnly.FromDateTime(to); d = d.AddDays(1))
        {
            var day = d;
            // multi-day items appear on their first visible day only, so the agenda stays dense
            var list = entries.Where(e => e.Item.StartDate == day || (e.Item.StartDate < day && day == DateOnly.FromDateTime(from) && e.Item.EndDate >= day)).ToList();
            if (list.Count == 0 && day != today) continue;
            var group = new AgendaGroup(OrgText.Day(day, today, Ctx.Loc), day.ToString("D", c), day == today, list);
            Agenda.Add(group);
        }
        AgendaRows.Clear();
        foreach (var g in Agenda)
        {
            AgendaRows.Add(g);
            foreach (var e in g.Entries) AgendaRows.Add(e);
        }
    }

    // ───────────── Today ─────────────

    private async Task BuildTodayAsync(List<CalendarEntry> entries, CancellationToken ct)
    {
        var loc = Ctx.Loc;
        var now = Ctx.Now;
        var today = Ctx.Today;
        Today.DateTitle = today.ToString("dddd", loc.Culture);
        Today.DateSubtitle = today.ToString("D", loc.Culture);
        Today.Timeline.Clear();
        foreach (var e in entries.Where(e => !e.IsAllDay).OrderBy(e => e.Item.Start))
        {
            e.IsPast = (e.Item.End > e.Item.Start ? e.Item.End : e.Item.Start) <= now;
            Today.Timeline.Add(e);
        }
        Today.AllDay.Clear();
        foreach (var e in entries.Where(e => e.IsAllDay && e.Item.Kind != CalendarItemKind.Trip)) Today.AllDay.Add(e);
        var trip = entries.FirstOrDefault(e => e.Item.Kind == CalendarItemKind.Trip);
        Today.ActiveTrip = trip;
        Today.ActiveTripText = trip == null ? null : loc.Format("Today_TripDay", today.DayNumber - trip.Item.StartDate.DayNumber + 1, trip.Item.EndDate.DayNumber - trip.Item.StartDate.DayNumber + 1);
        Today.Next = entries.Where(e => !e.IsAllDay && e.Item.Start >= now && !e.IsDone).OrderBy(e => e.Item.Start).FirstOrDefault();
        Today.NextText = Today.Next is { } n ? loc.Format("Today_InTime", Relative(n.Item.Start - now)) : null;
        Today.NowIndex = Today.Timeline.TakeWhile(e => e.Item.Start < now).Count();

        // tasks: overdue first, then today's (the store's Today view)
        var tasks = await _tasks.QueryAsync(new TaskQuery(TaskView.Today, Take: 12), now, ct);
        var taskChips = await ContextChip.ResolveAsync(Ctx, tasks.Select(t => t.Context).OfType<EntityRef>());
        Today.Tasks.Clear();
        foreach (var t in tasks)
            Today.Tasks.Add(new TaskListItem(t, t.Context is { } r ? taskChips.GetValueOrDefault(r) : null, OrgText.Due(t.DueDate, t.DueTime, now, loc),
                OrgText.DueTone(t.Status, t.DueDate, t.DueTime, now), t.SubtaskCount > 0 ? $"{t.SubtasksDone}/{t.SubtaskCount}" : null, t.ParentTaskId != null));
        var counts = await _tasks.GetCountsAsync(now, ct);
        Today.TaskSummary = counts.Overdue > 0 ? loc.Format("Task_SummaryOverdue", counts.Today, counts.Overdue) : loc.Format("Task_Summary", counts.Today, counts.CompletedToday);

        // deadlines ahead: project dates, task due dates and trips in the next two weeks
        var ahead = await _query.GetItemsAsync(today.AddDays(1).ToDateTime(TimeOnly.MinValue), today.AddDays(15).ToDateTime(TimeOnly.MinValue),
            ActiveSources & (CalendarSources.Projects | CalendarSources.Travel | CalendarSources.Events), ct);
        Today.Ahead.Clear();
        foreach (var i in ahead.Where(i => i.StartDate > today && (i.Kind is CalendarItemKind.ProjectDeadline or CalendarItemKind.Trip || (i.Kind == CalendarItemKind.Event && i.IsAllDay))).Take(6))
            Today.Ahead.Add(new AheadItem(new CalendarEntry(i, loc, null), OrgText.Day(i.StartDate, today, loc)));

        // notes: pinned, then the most recently edited
        Today.Notes.Clear();
        var pinned = await _notes.QueryAsync(new NoteQuery(NoteView.Pinned, Take: 3), ct);
        var recent = await _notes.QueryAsync(new NoteQuery(NoteView.Recent, Take: 4), ct);
        foreach (var note in pinned.Concat(recent.Where(r => pinned.All(p => p.Id != r.Id))).Take(4)) Today.Notes.Add(note);

        var reminders = entries.Count(e => e.HasReminder && e.Item.Start >= now);
        Today.Summary = loc.Format("Today_Summary", Today.Timeline.Count + Today.AllDay.Count, tasks.Count(t => !t.IsDone), reminders);
        Today.IsQuiet = Today.Timeline.Count == 0 && Today.AllDay.Count == 0 && Today.Tasks.Count == 0;
    }

    /// <summary>"in 12 min", "in 1 h 50 min", "in 3 days".</summary>
    private string Relative(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));
        if (minutes < 60) return Ctx.Loc.Format("Today_Minutes", minutes);
        if (span.TotalHours < 24)
        {
            var h = minutes / 60;
            var m = minutes % 60;
            return m == 0 ? Ctx.Loc.Format("Today_Hours", h) : Ctx.Loc.Format("Today_HoursMinutes", h, m);
        }
        return Ctx.Loc.Format("Today_Days", (int)Math.Round(span.TotalDays));
    }

    // ───────────── entries ─────────────

    [RelayCommand]
    private async Task OpenEntryAsync(CalendarEntry? entry)
    {
        if (entry == null) return;
        switch (entry.Item.Kind)
        {
            case CalendarItemKind.Event: await EditEventAsync(entry); break;
            default: Ctx.Navigation.Open(entry.Item.Ref); break;
        }
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(object? item)
    {
        (int id, bool done) = item switch
        {
            CalendarEntry e when e.IsTask => (e.Item.Ref.Id, !e.Item.IsDone),
            TaskListItem t => (t.Id, !t.Row.IsDone),
            _ => (0, false),
        };
        if (id == 0) return;
        if (await Ctx.TryAsync(() => _tasks.SetStatusAsync(id, done ? TaskItemStatus.Completed : TaskItemStatus.ToDo), "Task_ErrSave"))
            Ctx.Changed(OrganizerChange.Tasks);
        else await LoadAsync();
    }

    [RelayCommand]
    private Task NewEventAsync() => NewEventAtAsync(null);

    /// <summary>New event on a day (all-day in the month grid) or at a time (week/day grid, rounded to 30 minutes).</summary>
    public async Task NewEventAtAsync(DateTime? at, bool allDay = false)
    {
        await EnsureCategoriesAsync();
        var start = at ?? DefaultStart();
        if (!allDay) start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute >= 30 ? 30 : 0, 0);
        var vm = new EventEditorViewModel(Ctx, _categories, null, start, allDay ? start : start.AddHours(1), allDay);
        if (!Ctx.Dialogs.Show(vm) || vm.Draft is not { } draft) return;
        CalendarEvent? created = null;
        if (await Ctx.TryAsync(async () => created = await _store.CreateEventAsync(draft), "Evt_ErrSave"))
        {
            Ctx.Changed(OrganizerChange.Events);
            Ctx.Notifications.Show(Ctx.Loc.Format("Evt_Created", created!.Title), NotificationKind.Success);
        }
    }

    [RelayCommand]
    private Task NewEventOnDayAsync(object? day) => day switch
    {
        MonthDay m => NewEventAtAsync(m.Date.ToDateTime(new TimeOnly(9, 0))),
        DayColumn c => NewEventAtAsync(c.Date.ToDateTime(TimeOnly.MinValue), allDay: true),
        _ => NewEventAtAsync(null),
    };

    private DateTime DefaultStart()
    {
        var now = Ctx.Now;
        var day = View is CalendarView.Month ? SelectedDay : View is CalendarView.Today ? Ctx.Today : Anchor;
        if (day == Ctx.Today) return now.AddMinutes(60 - now.Minute); // the next full hour
        return day.ToDateTime(new TimeOnly(9, 0));
    }

    [RelayCommand]
    private async Task NewTaskOnDayAsync(object? day)
    {
        var date = day switch { MonthDay m => m.Date, DayColumn c => c.Date, _ => SelectedDay };
        var title = Ctx.Loc["Task_NewTitle"];
        if (await Ctx.TryAsync(async () => { var t = await _tasks.CreateAsync(new TaskItemDraft(title, DueDate: date)); Ctx.Navigation.Open(new EntityRef(EntityKind.Task, t.Id)); }, "Task_ErrSave"))
            Ctx.Changed(OrganizerChange.Tasks);
    }

    public async Task EditEventAsync(CalendarEntry entry)
    {
        var ev = await _store.GetEventAsync(entry.Item.Ref.Id);
        if (ev == null) { Ctx.Notifications.Show(Ctx.Loc["Org_ErrGone"], NotificationKind.Warning); await LoadAsync(); return; }
        await EnsureCategoriesAsync();
        var occurrence = ev.IsRecurring && entry.Item.OccurrenceDate is { } od
            ? Recurrence.Expand(ev, od.ToDateTime(TimeOnly.MinValue).AddDays(-1), entry.Item.End.AddDays(1)).FirstOrDefault(o => o.OriginalDate == od)
            : null;
        var chips = ev.Context is { } r ? await ContextChip.ResolveAsync(Ctx, [r]) : null;
        var vm = new EventEditorViewModel(Ctx, _categories, ev, ev.Start, ev.End, ev.IsAllDay, occurrence, chips);
        if (!Ctx.Dialogs.Show(vm)) return;
        if (vm.DeleteRequested) { await DeleteEventAsync(ev, occurrence?.OriginalDate); return; }
        if (vm.Draft is not { } draft) return;
        var scope = RecurrenceScope.AllEvents;
        if (ev.IsRecurring && occurrence != null)
        {
            var ask = new RecurrenceScopeViewModel(Ctx.Loc, deleting: false, isFirst: occurrence.OriginalDate == DateOnly.FromDateTime(ev.Start));
            if (!Ctx.Dialogs.Show(ask) || ask.Scope is not { } s) return;
            scope = s;
        }
        if (await Ctx.TryAsync(() => _store.UpdateEventAsync(ev.Id, draft, scope, occurrence?.OriginalDate), "Evt_ErrSave"))
            Ctx.Changed(OrganizerChange.Events);
    }

    [RelayCommand]
    private async Task DeleteEntryAsync(CalendarEntry? entry)
    {
        if (entry is not { IsEvent: true }) return;
        var ev = await _store.GetEventAsync(entry.Item.Ref.Id);
        if (ev != null) await DeleteEventAsync(ev, ev.IsRecurring ? entry.Item.OccurrenceDate : null);
    }

    private async Task DeleteEventAsync(CalendarEvent ev, DateOnly? occurrence)
    {
        var scope = RecurrenceScope.AllEvents;
        if (ev.IsRecurring && occurrence is { } od)
        {
            var ask = new RecurrenceScopeViewModel(Ctx.Loc, deleting: true, isFirst: od == DateOnly.FromDateTime(ev.Start));
            if (!Ctx.Dialogs.Show(ask) || ask.Scope is not { } s) return;
            scope = s;
        }
        else if (!Ctx.Dialogs.Confirm(Ctx.Loc.Format("Evt_ConfirmDelete", ev.Title), Ctx.Loc["Confirm_Delete"])) return;
        if (await Ctx.TryAsync(() => _store.DeleteEventAsync(ev.Id, scope, occurrence), "Evt_ErrSave"))
            Ctx.Changed(OrganizerChange.Events);
    }

    /// <summary>Opens an event from search, a reminder or Home: its day, then the editor.</summary>
    public async Task ShowEventAsync(int id, DateOnly? occurrence = null)
    {
        var ev = await _store.GetEventAsync(id);
        if (ev == null) { Ctx.Notifications.Show(Ctx.Loc["Org_ErrGone"], NotificationKind.Warning); return; }
        var day = occurrence ?? (ev.IsRecurring ? Recurrence.Expand(ev, Ctx.Now.Date, Ctx.Now.Date.AddYears(2)).FirstOrDefault()?.OriginalDate : null) ?? DateOnly.FromDateTime(ev.Start);
        await ShowDateAsync(day, View is CalendarView.Month or CalendarView.Week ? View : CalendarView.Day);
        var entry = Columns.SelectMany(c => c.AllDay.Concat(c.Timed.Select(t => t.Entry))).Concat(SelectedDayEntries)
            .FirstOrDefault(e => e.IsEvent && e.Item.Ref.Id == id);
        if (entry != null) await EditEventAsync(entry);
    }

    // ───────────── categories ─────────────

    private async Task EnsureCategoriesAsync()
    {
        if (_categories.Count == 0) await LoadCategoriesAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        try
        {
            _categories = await _store.GetCategoriesAsync();
            Categories.Clear();
            foreach (var c in _categories) Categories.Add(new CategoryRow(c, EventEditorViewModel.CategoryName(c, Ctx.Loc)));
        }
        catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Loading calendar categories failed"); }
    }

    [RelayCommand]
    private async Task EditCategoryAsync(CategoryRow? row)
    {
        var vm = new CategoryEditorViewModel(Ctx.Loc, row?.Category);
        if (!Ctx.Dialogs.Show(vm)) return;
        if (await Ctx.TryAsync(() => _store.SaveCategoryAsync(row?.Category.Id, vm.Name, vm.Glyph, vm.Accent), "Evt_ErrSave"))
        {
            await LoadCategoriesAsync();
            Ctx.Changed(OrganizerChange.Events);
        }
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync(CategoryRow? row)
    {
        if (row == null || !Ctx.Dialogs.Confirm(Ctx.Loc.Format("Cat_ConfirmDelete", row.Name), Ctx.Loc["Confirm_Delete"])) return;
        if (await Ctx.TryAsync(() => _store.DeleteCategoryAsync(row.Category.Id), "Evt_ErrSave"))
        {
            await LoadCategoriesAsync();
            Ctx.Changed(OrganizerChange.Events);
        }
    }

    [RelayCommand] private void OpenNote(NoteRow? note) { if (note != null) Ctx.Navigation.Open(new EntityRef(EntityKind.Note, note.Id)); }
    [RelayCommand] private void OpenTask(TaskListItem? task) { if (task != null) Ctx.Navigation.Open(new EntityRef(EntityKind.Task, task.Id)); }
    [RelayCommand] private void OpenAhead(AheadItem? item) { if (item != null) _ = OpenEntryAsync(item.Entry); }

    public bool OnNew()
    {
        _ = NewEventAtAsync(null);
        return true;
    }

    public bool OnSave() => false;
}

public sealed record AheadItem(CalendarEntry Entry, string DayText)
{
    public override string ToString() => $"{DayText} {Entry.Title}";
}

/// <summary>What the Today view shows (rebuilt on every load).</summary>
public sealed partial class TodayState : ObservableObject
{
    public ObservableCollection<CalendarEntry> Timeline { get; } = [];
    public ObservableCollection<CalendarEntry> AllDay { get; } = [];
    public ObservableCollection<TaskListItem> Tasks { get; } = [];
    public ObservableCollection<AheadItem> Ahead { get; } = [];
    public ObservableCollection<NoteRow> Notes { get; } = [];
    [ObservableProperty] private string _dateTitle = string.Empty;
    [ObservableProperty] private string _dateSubtitle = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _taskSummary = string.Empty;
    [ObservableProperty] private CalendarEntry? _next;
    [ObservableProperty] private string? _nextText;
    [ObservableProperty] private CalendarEntry? _activeTrip;
    [ObservableProperty] private string? _activeTripText;
    [ObservableProperty] private int _nowIndex;
    [ObservableProperty] private bool _isQuiet;
}
