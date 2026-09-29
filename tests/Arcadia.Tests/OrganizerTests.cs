using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Travel;
using Arcadia.Data;
using Arcadia.Data.Organizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class RecurrenceTests
{
    private static CalendarEvent Series(DateTime start, TimeSpan length, RecurrenceFrequency f, int interval = 1, WeekdayMask days = WeekdayMask.None, DateOnly? until = null) =>
        new() { Id = 1, Title = "Series", Start = start, End = start + length, Frequency = f, Interval = interval, Weekdays = days, RecursUntil = until };

    private static List<DateOnly> Dates(CalendarEvent e, DateTime from, DateTime to) => Recurrence.Expand(e, from, to).Select(o => DateOnly.FromDateTime(o.Start)).ToList();

    [Fact]
    public void Weekly_on_several_days_every_other_week()
    {
        // Monday 28 Sep 2026, Mon/Wed/Fri every 2 weeks
        var e = Series(new DateTime(2026, 9, 28, 10, 0, 0), TimeSpan.FromHours(1), RecurrenceFrequency.Weekly, 2, WeekdayMask.Monday | WeekdayMask.Wednesday | WeekdayMask.Friday);
        Assert.Equal(new[] { D(2026, 9, 28), D(2026, 9, 30), D(2026, 10, 2), D(2026, 10, 12), D(2026, 10, 14), D(2026, 10, 16) },
            Dates(e, new DateTime(2026, 9, 28), new DateTime(2026, 10, 19)));
        Assert.All(Recurrence.Expand(e, new DateTime(2026, 9, 28), new DateTime(2026, 10, 19)), o => Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(o.Start)));
    }

    [Fact]
    public void Weekdays_rule_skips_weekends()
    {
        var e = Series(new DateTime(2026, 10, 2, 8, 0, 0), TimeSpan.FromMinutes(30), RecurrenceFrequency.Weekly, 1, WeekdayMask.Weekdays);
        Assert.Equal(new[] { D(2026, 10, 2), D(2026, 10, 5), D(2026, 10, 6) }, Dates(e, new DateTime(2026, 10, 1), new DateTime(2026, 10, 7)));
    }

    [Fact]
    public void Monthly_on_the_31st_clamps_to_the_last_day_and_yearly_29_February_to_28()
    {
        var monthly = Series(new DateTime(2026, 1, 31, 9, 0, 0), TimeSpan.FromHours(1), RecurrenceFrequency.Monthly);
        Assert.Equal(new[] { D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30) }, Dates(monthly, new DateTime(2026, 1, 1), new DateTime(2026, 5, 1)));

        var leap = Series(new DateTime(2028, 2, 29), TimeSpan.Zero, RecurrenceFrequency.Yearly);
        leap.IsAllDay = true;
        leap.End = leap.Start;
        Assert.Equal(new[] { D(2028, 2, 29), D(2029, 2, 28), D(2032, 2, 29) }, Dates(leap, new DateTime(2028, 1, 1), new DateTime(2033, 1, 1)).Where(d => d.Year is 2028 or 2029 or 2032).ToList());
    }

    [Fact]
    public void Custom_interval_and_end_date_are_respected()
    {
        var e = Series(new DateTime(2026, 9, 1, 7, 0, 0), TimeSpan.FromHours(1), RecurrenceFrequency.Daily, 3, until: D(2026, 9, 10));
        Assert.Equal(new[] { D(2026, 9, 1), D(2026, 9, 4), D(2026, 9, 7), D(2026, 9, 10) }, Dates(e, new DateTime(2026, 8, 1), new DateTime(2026, 12, 1)));
    }

    [Fact]
    public void A_series_running_for_years_expands_only_the_requested_range()
    {
        var e = Series(new DateTime(2010, 1, 1, 6, 0, 0), TimeSpan.FromMinutes(20), RecurrenceFrequency.Daily);
        var week = Recurrence.Expand(e, new DateTime(2026, 9, 28), new DateTime(2026, 10, 5));
        Assert.Equal(7, week.Count);
        Assert.Equal(new DateTime(2026, 9, 28, 6, 0, 0), week[0].Start);
        Assert.Empty(Recurrence.Expand(e, new DateTime(2009, 1, 1), new DateTime(2009, 12, 31)));
    }

    [Fact]
    public void Exceptions_cancel_move_and_rename_single_occurrences()
    {
        var e = Series(new DateTime(2026, 10, 5, 9, 0, 0), TimeSpan.FromHours(1), RecurrenceFrequency.Daily);
        e.Exceptions.Add(new CalendarEventException { OriginalDate = D(2026, 10, 6), IsCancelled = true });
        e.Exceptions.Add(new CalendarEventException { OriginalDate = D(2026, 10, 7), Start = new DateTime(2026, 10, 7, 15, 0, 0), End = new DateTime(2026, 10, 7, 16, 30, 0), Title = "Moved" });
        // moved out of its own day into a range that does not contain the original date
        e.Exceptions.Add(new CalendarEventException { OriginalDate = D(2026, 10, 20), Start = new DateTime(2026, 10, 8, 20, 0, 0), End = new DateTime(2026, 10, 8, 21, 0, 0) });

        var list = Recurrence.Expand(e, new DateTime(2026, 10, 5), new DateTime(2026, 10, 9));
        Assert.DoesNotContain(list, o => o.OriginalDate == D(2026, 10, 6));
        var moved = Assert.Single(list, o => o.OriginalDate == D(2026, 10, 7));
        Assert.Equal("Moved", moved.Title);
        Assert.Equal(new DateTime(2026, 10, 7, 16, 30, 0), moved.End);
        Assert.Contains(list, o => o.OriginalDate == D(2026, 10, 20) && o.Start == new DateTime(2026, 10, 8, 20, 0, 0));
        Assert.Equal(list.OrderBy(o => o.Start).Select(o => o.Start), list.Select(o => o.Start));
    }

    [Fact]
    public void Splitting_a_series_ends_the_old_part_and_moves_later_exceptions()
    {
        var e = Series(new DateTime(2026, 10, 1, 9, 0, 0), TimeSpan.FromHours(1), RecurrenceFrequency.Weekly);
        e.Exceptions.Add(new CalendarEventException { OriginalDate = D(2026, 10, 8), IsCancelled = true });
        e.Exceptions.Add(new CalendarEventException { OriginalDate = D(2026, 10, 22), IsCancelled = true });
        var tail = Recurrence.SplitAt(e, D(2026, 10, 15))!;
        Assert.Equal(D(2026, 10, 14), e.RecursUntil);
        Assert.Equal(new DateTime(2026, 10, 15, 9, 0, 0), tail.Start);
        Assert.Single(e.Exceptions);
        Assert.Equal(D(2026, 10, 22), Assert.Single(tail.Exceptions).OriginalDate);
        Assert.Null(Recurrence.SplitAt(e, D(2026, 10, 1))); // the first occurrence: edit the whole series instead
    }

    [Fact]
    public void Multi_day_all_day_events_appear_on_every_day_they_cover()
    {
        var e = new CalendarEvent { Id = 3, Title = "Conference", IsAllDay = true, Start = new DateTime(2026, 10, 7), End = new DateTime(2026, 10, 9) };
        Assert.Single(Recurrence.Expand(e, new DateTime(2026, 10, 9), new DateTime(2026, 10, 10)));
        Assert.Empty(Recurrence.Expand(e, new DateTime(2026, 10, 10), new DateTime(2026, 10, 11)));
        Assert.Equal(TimeSpan.FromDays(3), Recurrence.Duration(e));
    }

    private static DateOnly D(int y, int m, int d) => new(y, m, d);
}

public sealed class QuickDateParserTests
{
    // Wednesday 30 September 2026, 10:00
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0);

    [Theory]
    [InlineData("Call dentist tomorrow", "Call dentist", "2026-10-01", null)]
    [InlineData("Backup server Friday", "Backup server", "2026-10-02", null)]
    [InlineData("Pack bags next Monday", "Pack bags", "2026-10-05", null)]
    [InlineData("Review next Friday", "Review", "2026-10-09", null)]
    [InlineData("Finish report 18:00", "Finish report", "2026-09-30", "18:00")]
    [InlineData("Water plants at 9:00", "Water plants", "2026-10-01", "09:00")] // 09:00 has passed: tomorrow
    [InlineData("Standup tomorrow at 9", "Standup", "2026-10-01", "09:00")]
    [InlineData("Dentist Friday at 6:30pm", "Dentist", "2026-10-02", "18:30")]
    [InlineData("Invoice in 3 days", "Invoice", "2026-10-03", null)]
    [InlineData("Renew passport 12 Oct", "Renew passport", "2026-10-12", null)]
    [InlineData("Taxes due 2027-01-15", "Taxes", "2027-01-15", null)]
    [InlineData("Birthday 5.3.", "Birthday", "2027-03-05", null)] // German day.month, already past this year
    [InlineData("Zahnarzt morgen um 18 Uhr", "Zahnarzt", "2026-10-01", "18:00")]
    [InlineData("اتصل بالطبيب غدا", "اتصل بالطبيب", "2026-10-01", null)]
    [InlineData("Wednesday sync", "sync", "2026-10-07", null)] // today is Wednesday: the next one
    public void Understands_common_phrases(string text, string title, string date, string? time)
    {
        var r = QuickDateParser.Parse(text, Now);
        Assert.Equal(title, r.Title);
        Assert.Equal(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), r.Date);
        Assert.Equal(time == null ? null : TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture), r.Time);
        Assert.False(r.Ambiguous);
    }

    [Theory]
    [InlineData("Buy SSD for server")]
    [InlineData("Buy 2 SSDs and read chapter 12")]
    [InlineData("Do the dishes")] // "do" is not Thursday
    public void Leaves_text_without_dates_alone(string text)
    {
        var r = QuickDateParser.Parse(text, Now);
        Assert.Equal(text, r.Title);
        Assert.Null(r.Date);
        Assert.Null(r.Time);
    }

    [Fact]
    public void Slash_dates_are_ambiguous_and_kept_in_the_text()
    {
        var r = QuickDateParser.Parse("Call bank 10/12", Now);
        Assert.True(r.Ambiguous);
        Assert.Null(r.Date);
        Assert.Contains("10/12", r.Title);
    }
}

public sealed class OrganizerRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0);

    [Fact]
    public void Overdue_depends_on_the_due_time()
    {
        var today = DateOnly.FromDateTime(Now);
        Assert.True(TaskRules.IsOverdue(TaskItemStatus.ToDo, today.AddDays(-1), null, Now));
        Assert.False(TaskRules.IsOverdue(TaskItemStatus.ToDo, today, null, Now)); // due some time today
        Assert.True(TaskRules.IsOverdue(TaskItemStatus.InProgress, today, new TimeOnly(9, 0), Now));
        Assert.False(TaskRules.IsOverdue(TaskItemStatus.ToDo, today, new TimeOnly(11, 0), Now));
        Assert.False(TaskRules.IsOverdue(TaskItemStatus.Completed, today.AddDays(-3), null, Now));
        Assert.True(TaskRules.IsUpcoming(TaskItemStatus.ToDo, today.AddDays(3), Now, 7));
    }

    [Fact]
    public void Reminders_are_planned_per_occurrence_and_for_tasks()
    {
        var daily = new CalendarEvent { Id = 1, Title = "Pills", Start = new DateTime(2026, 9, 1, 8, 0, 0), End = new DateTime(2026, 9, 1, 8, 5, 0), Frequency = RecurrenceFrequency.Daily, ReminderMinutes = 15 };
        var oneOff = new CalendarEvent { Id = 2, Title = "Dentist", Start = new DateTime(2026, 10, 1, 9, 0, 0), End = new DateTime(2026, 10, 1, 10, 0, 0), ReminderMinutes = 60 * 24 };
        var allDay = new CalendarEvent { Id = 3, Title = "Holiday", IsAllDay = true, Start = new DateTime(2026, 10, 1), End = new DateTime(2026, 10, 1), ReminderMinutes = 0 };
        var silent = new CalendarEvent { Id = 4, Title = "No reminder", Start = new DateTime(2026, 9, 30, 12, 0, 0), End = new DateTime(2026, 9, 30, 13, 0, 0) };
        var task = new TaskItem { Id = 7, Title = "Call back", ReminderAt = new DateTime(2026, 9, 30, 16, 0, 0) };
        var done = new TaskItem { Id = 8, Title = "Done", ReminderAt = new DateTime(2026, 9, 30, 16, 0, 0), Status = TaskItemStatus.Completed };

        var due = ReminderPlanner.Due([daily, oneOff, allDay, silent], [task, done], Now, new DateTime(2026, 10, 1, 10, 0, 0));
        Assert.Equal(new[]
        {
            (ReminderSource.Task, 7, new DateTime(2026, 9, 30, 16, 0, 0)),
            (ReminderSource.Event, 1, new DateTime(2026, 10, 1, 7, 45, 0)),
            (ReminderSource.Event, 3, new DateTime(2026, 10, 1, 9, 0, 0)),
        }, due.Select(d => (d.Source, d.Id, d.TriggerAt)).ToArray());
        Assert.Equal(new DateOnly(2026, 10, 1), due[1].OccurrenceDate);
        // the dentist reminder (one day before) fell before the window
        Assert.Contains(ReminderPlanner.Due([oneOff], [], new DateTime(2026, 9, 30, 8, 0, 0), new DateTime(2026, 9, 30, 9, 30, 0)), d => d.Id == 2);
    }

    [Fact]
    public void The_reminder_service_sleeps_until_the_next_reminder_never_polls_and_rechecks_within_six_hours()
    {
        var now = new DateTime(2026, 9, 30, 10, 0, 0);
        Assert.Equal(TimeSpan.FromMinutes(20), Arcadia.App.Services.ReminderService.SleepFor(now, now.AddMinutes(20)));
        Assert.Equal(Arcadia.App.Services.ReminderService.MaxSleep, Arcadia.App.Services.ReminderService.SleepFor(now, now.AddDays(3)));
        Assert.Equal(Arcadia.App.Services.ReminderService.MaxSleep, Arcadia.App.Services.ReminderService.SleepFor(now, null));
        Assert.Equal(TimeSpan.FromSeconds(1), Arcadia.App.Services.ReminderService.SleepFor(now, now.AddSeconds(-30))); // overdue: right away, but never a tight loop
    }

    [Fact]
    public void Stored_contexts_are_read_back_safely()
    {
        Assert.Equal(new EntityRef(EntityKind.Trip, 4), EntityRef.From("Trip", 4));
        Assert.Null(EntityRef.From("Spaceship", 4));
        Assert.Null(EntityRef.From("Trip", null));
        Assert.Null(EntityRef.From(null, 4));
    }

    [Fact]
    public void Arcadia_links_round_trip_and_reject_anything_else()
    {
        var r = new EntityRef(EntityKind.Trip, 12);
        Assert.Equal("arcadia://trip/12", r.ToString());
        Assert.True(EntityRef.TryParse("ARCADIA://Trip/12/", out var back));
        Assert.Equal(r, back);
        Assert.True(EntityRef.TryParse("arcadia://session/3", out var s));
        Assert.Equal(EntityKind.Diagnostic, s.Kind);
        Assert.False(EntityRef.TryParse("arcadia://trip/-1", out _));
        Assert.False(EntityRef.TryParse("arcadia://unknown/1", out _));
        Assert.False(EntityRef.TryParse("https://example.com/trip/1", out _));
        Assert.False(EntityRef.TryParse("arcadia://trip/1/extra", out _));
    }
}

public sealed class OrganizerStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private readonly MovableClock _clock = new(new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));
    private TaskStore _tasks = null!;
    private NoteStore _notes = null!;
    private CalendarStore _calendar = null!;
    private CalendarQueryService _query = null!;
    private ProjectStore _projects = null!;
    private TravelStore _travel = null!;
    private GlobalSearchService _search = null!;
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0);
    private static readonly DateOnly Today = new(2026, 9, 30);

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    internal sealed class MovableClock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Utc);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _tasks = new TaskStore(_factory, _clock);
        _notes = new NoteStore(_factory, _clock);
        _calendar = new CalendarStore(_factory, _clock);
        _query = new CalendarQueryService(_factory, _clock);
        _projects = new ProjectStore(_factory, _clock);
        _travel = new TravelStore(_factory, _clock, null);
        _search = new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(_factory)]);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private Task<Project> NewProject(string name) => _projects.CreateAsync(new ProjectDraft(name, null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Blue", "Projects", "mountains-dusk", "", null));

    // ───────────── events ─────────────

    [Fact]
    public async Task Events_are_created_validated_found_and_deleted()
    {
        var ev = await _calendar.CreateEventAsync(new EventDraft("Dentist", Now.AddDays(1).Date.AddHours(9), Now.AddDays(1).Date.AddHours(10), Location: "Dr. Weber, Hauptstraße 3",
            CategoryId: 6, ReminderMinutes: 30, Tags: "health"));
        var loaded = (await _calendar.GetEventAsync(ev.Id))!;
        Assert.Equal("Dentist", loaded.Title);
        Assert.Equal("appointment", loaded.Category!.Key);
        Assert.Equal(30, loaded.ReminderMinutes);

        await Assert.ThrowsAsync<ArgumentException>(() => _calendar.CreateEventAsync(new EventDraft(" ", Now, Now)));
        await Assert.ThrowsAsync<ArgumentException>(() => _calendar.CreateEventAsync(new EventDraft("Backwards", Now, Now.AddHours(-1))));
        await Assert.ThrowsAsync<ArgumentException>(() => _calendar.CreateEventAsync(new EventDraft("Bad", Now, Now, Frequency: RecurrenceFrequency.Daily, Interval: 0)));

        var hit = Assert.Single(await _search.SearchAsync(new SearchRequest("Weber")), h => h.Kind == SearchResultKind.Event);
        Assert.Equal(ev.Id, hit.Id);
        Assert.Contains(await _query.SearchEventsAsync("appointment"), i => i.Ref.Id == ev.Id); // by category
        Assert.Contains(await _query.SearchEventsAsync("health"), i => i.Ref.Id == ev.Id); // by tag

        await _calendar.DeleteEventAsync(ev.Id);
        Assert.Null(await _calendar.GetEventAsync(ev.Id));
        Assert.DoesNotContain(await _search.SearchAsync(new SearchRequest("Weber")), h => h.Kind == SearchResultKind.Event);
    }

    [Fact]
    public async Task Editing_one_occurrence_this_and_future_or_all_of_a_series()
    {
        var start = new DateTime(2026, 10, 5, 18, 0, 0); // Monday
        var series = await _calendar.CreateEventAsync(new EventDraft("Training", start, start.AddHours(1), Frequency: RecurrenceFrequency.Weekly, Weekdays: WeekdayMask.Monday));
        async Task<List<(DateTime Start, string Title)>> Occ() =>
            (await _query.GetItemsAsync(new DateTime(2026, 10, 1), new DateTime(2026, 11, 3), CalendarSources.Events)).Select(i => (i.Start, i.Title)).ToList();
        Assert.Equal(5, (await Occ()).Count); // 5, 12, 19, 26 Oct, 2 Nov

        // this event: 12 Oct moves to 19:30 and gets a new title
        await _calendar.UpdateEventAsync(series.Id, new EventDraft("Training (late)", new DateTime(2026, 10, 12, 19, 30, 0), new DateTime(2026, 10, 12, 20, 30, 0),
            Frequency: RecurrenceFrequency.Weekly, Weekdays: WeekdayMask.Monday), RecurrenceScope.ThisEvent, new DateOnly(2026, 10, 12));
        var o = await Occ();
        Assert.Contains((new DateTime(2026, 10, 12, 19, 30, 0), "Training (late)"), o);
        Assert.Contains((new DateTime(2026, 10, 19, 18, 0, 0), "Training"), o);

        // this and future from 26 Oct: new time 07:00, the old series ends on 25 Oct
        var tailId = await _calendar.UpdateEventAsync(series.Id, new EventDraft("Morning training", new DateTime(2026, 10, 26, 7, 0, 0), new DateTime(2026, 10, 26, 8, 0, 0),
            Frequency: RecurrenceFrequency.Weekly, Weekdays: WeekdayMask.Monday), RecurrenceScope.ThisAndFuture, new DateOnly(2026, 10, 26));
        Assert.NotEqual(series.Id, tailId);
        o = await Occ();
        Assert.Equal(new[] { "Training", "Training (late)", "Training", "Morning training", "Morning training" }, o.Select(x => x.Title));
        Assert.Equal(new DateOnly(2026, 10, 25), (await _calendar.GetEventAsync(series.Id))!.RecursUntil);

        // all events of the first part: the time changes, the moved occurrence keeps its own time
        await _calendar.UpdateEventAsync(series.Id, new EventDraft("Training", new DateTime(2026, 10, 19, 18, 30, 0), new DateTime(2026, 10, 19, 19, 30, 0),
            Frequency: RecurrenceFrequency.Weekly, Weekdays: WeekdayMask.Monday, RecursUntil: new DateOnly(2026, 10, 25)), RecurrenceScope.AllEvents, new DateOnly(2026, 10, 19));
        o = await Occ();
        Assert.Contains((new DateTime(2026, 10, 5, 18, 30, 0), "Training"), o);
        Assert.Contains((new DateTime(2026, 10, 12, 19, 30, 0), "Training (late)"), o);

        // delete one occurrence, then this-and-future of the tail
        await _calendar.DeleteEventAsync(series.Id, RecurrenceScope.ThisEvent, new DateOnly(2026, 10, 5));
        await _calendar.DeleteEventAsync(tailId, RecurrenceScope.ThisAndFuture, new DateOnly(2026, 11, 2));
        o = await Occ();
        Assert.Equal(new[] { new DateTime(2026, 10, 12, 19, 30, 0), new DateTime(2026, 10, 19, 18, 30, 0), new DateTime(2026, 10, 26, 7, 0, 0) }, o.Select(x => x.Start));
        await _calendar.DeleteEventAsync(tailId, RecurrenceScope.AllEvents);
        Assert.Null(await _calendar.GetEventAsync(tailId));
    }

    // ───────────── tasks ─────────────

    [Fact]
    public async Task Tasks_belong_to_nothing_a_project_or_another_record_and_keep_project_progress()
    {
        var project = await NewProject("Arcadia Development");
        var free = await _tasks.CreateAsync(new TaskItemDraft("Buy SSD for server", DueDate: Today, Priority: ProjectPriority.High));
        var projectTask = await _tasks.CreateAsync(new TaskItemDraft("Finish Phase 6", DueDate: Today.AddDays(2), Context: new EntityRef(EntityKind.Project, project.Id)));
        var sub = await _tasks.CreateAsync(new TaskItemDraft("Write tests", ParentTaskId: projectTask.Id));
        var trip = await _travel.CreateTripAsync(new TripDraft("Italy 2027"));
        var tripTask = await _tasks.CreateAsync(new TaskItemDraft("Book the ferry", Context: new EntityRef(EntityKind.Trip, trip.Id)));

        Assert.Equal(project.Id, (await _tasks.GetAsync(sub.Id))!.ProjectId); // subtasks share the parent's context
        Assert.Equal(new EntityRef(EntityKind.Trip, trip.Id), (await _tasks.GetAsync(tripTask.Id))!.Context);
        await Assert.ThrowsAsync<ArgumentException>(() => _tasks.CreateAsync(new TaskItemDraft("Too deep", ParentTaskId: sub.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => _tasks.CreateAsync(new TaskItemDraft("Bad dates", StartDate: Today, DueDate: Today.AddDays(-1))));

        // the Projects module sees the unified tasks
        var loaded = (await _projects.GetAsync(project.Id))!;
        Assert.Equal(2, loaded.Tasks.Count);
        await _tasks.SetStatusAsync(sub.Id, TaskItemStatus.Completed);
        var summary = (await _projects.ListAsync()).Single(p => p.Id == project.Id);
        Assert.Equal(50, summary.Progress);
        Assert.Contains((await _projects.GetActivityAsync(project.Id, 20)), a => a.Kind == ProjectActivityKind.TaskCompleted && a.Subject == "Write tests");
        // free tasks do not count towards any project's statistics
        Assert.Equal(1, (await _projects.GetStatsAsync(Today, 14)).TasksRemaining);

        var today = await _tasks.QueryAsync(new TaskQuery(TaskView.Today), Now);
        Assert.Equal(new[] { "Buy SSD for server" }, today.Select(t => t.Title));
        Assert.Equal("Finish Phase 6", Assert.Single(await _tasks.QueryAsync(new TaskQuery(TaskView.Upcoming), Now)).Title);
        var byProject = Assert.Single(await _tasks.QueryAsync(new TaskQuery(TaskView.ByProject, ProjectId: project.Id), Now));
        Assert.Equal((1, 1), (byProject.SubtaskCount, byProject.SubtasksDone));
        Assert.Equal(new EntityRef(EntityKind.Project, project.Id), byProject.Context);
        Assert.Equal("Book the ferry", Assert.Single(await _tasks.QueryAsync(new TaskQuery(Context: new EntityRef(EntityKind.Trip, trip.Id)), Now)).Title);
        Assert.Equal("Buy SSD for server", Assert.Single(await _tasks.QueryAsync(new TaskQuery(Text: "ssd"), Now)).Title);

        await _tasks.DeleteAsync(projectTask.Id);
        Assert.Null(await _tasks.GetAsync(sub.Id));
        Assert.DoesNotContain(await _search.SearchAsync(new SearchRequest("tests")), h => h.Kind == SearchResultKind.Task);
    }

    [Fact]
    public async Task Overdue_completed_pinned_views_and_counts()
    {
        var late = await _tasks.CreateAsync(new TaskItemDraft("Renew insurance", DueDate: Today.AddDays(-2)));
        var earlier = await _tasks.CreateAsync(new TaskItemDraft("Morning call", DueDate: Today, DueTime: new TimeOnly(9, 0)));
        var later = await _tasks.CreateAsync(new TaskItemDraft("Evening run", DueDate: Today, DueTime: new TimeOnly(19, 0), IsPinned: true));
        await _tasks.CreateAsync(new TaskItemDraft("Next week", DueDate: Today.AddDays(5)));
        var done = await _tasks.CreateAsync(new TaskItemDraft("Done already"));
        await _tasks.SetStatusAsync(done.Id, TaskItemStatus.Completed);

        Assert.Equal(new[] { late.Id, earlier.Id }, (await _tasks.QueryAsync(new TaskQuery(TaskView.Overdue), Now)).Select(t => t.Id));
        Assert.Equal(new[] { late.Id, earlier.Id, later.Id }, (await _tasks.QueryAsync(new TaskQuery(TaskView.Today), Now)).Select(t => t.Id));
        Assert.Equal(later.Id, Assert.Single(await _tasks.QueryAsync(new TaskQuery(TaskView.Pinned), Now)).Id);
        var completed = Assert.Single(await _tasks.QueryAsync(new TaskQuery(TaskView.Completed), Now));
        Assert.NotNull(completed.CompletedAtUtc);

        var counts = await _tasks.GetCountsAsync(Now);
        Assert.Equal((2, 2, 1, 1, 4), (counts.Today, counts.Overdue, counts.Upcoming, counts.Pinned, counts.Open));

        await _tasks.SetDueAsync(late.Id, Today.AddDays(1), null);
        Assert.Equal(earlier.Id, Assert.Single(await _tasks.QueryAsync(new TaskQuery(TaskView.Overdue), Now)).Id);
    }

    // ───────────── notes ─────────────

    [Fact]
    public async Task Notes_autosave_keep_a_small_history_and_are_searchable()
    {
        var project = await NewProject("Home Lab");
        var note = await _notes.CreateAsync(new NoteDraft("Router configuration", "VLAN 10 for IoT", "network, homelab", new EntityRef(EntityKind.Project, project.Id)));
        Assert.Equal(project.Id, (await _projects.GetAsync(project.Id))!.Notes.Single().ProjectId);

        // quick successive saves: one revision (the state before the session)
        for (var i = 1; i <= 5; i++)
        {
            _clock.Utc = _clock.Utc.AddSeconds(5);
            await _notes.SaveAsync(note.Id, "Router configuration", "VLAN 10 for IoT\nstep " + i, "network, homelab");
        }
        Assert.Equal("VLAN 10 for IoT", Assert.Single(await _notes.GetRevisionsAsync(note.Id)).Content);
        // after a pause, the next save keeps another; never more than the cap
        for (var i = 0; i < NoteStore.MaxRevisions + 3; i++)
        {
            _clock.Utc = _clock.Utc.AddMinutes(15);
            await _notes.SaveAsync(note.Id, "Router configuration", "version " + i, "network, homelab");
        }
        Assert.Equal(NoteStore.MaxRevisions, (await _notes.GetRevisionsAsync(note.Id)).Count);

        var general = await _notes.CreateAsync(new NoteDraft("Books to read", "- Dune\n- **Hyperion**", "reading"));
        await _notes.SetPinnedAsync(general.Id, true);
        await _notes.SetFavoriteAsync(note.Id, true);
        Assert.Equal(general.Id, Assert.Single(await _notes.QueryAsync(new NoteQuery(NoteView.Pinned))).Id);
        Assert.Equal(note.Id, Assert.Single(await _notes.QueryAsync(new NoteQuery(NoteView.Favorites))).Id);
        Assert.Equal(note.Id, Assert.Single(await _notes.QueryAsync(new NoteQuery(Module: EntityKind.Project))).Id);
        Assert.Equal(general.Id, Assert.Single(await _notes.QueryAsync(new NoteQuery(GeneralOnly: true))).Id);
        Assert.Equal(note.Id, Assert.Single(await _notes.QueryAsync(new NoteQuery(NoteView.ByTag, Tag: "homelab"))).Id);
        Assert.Equal("Dune Hyperion", (await _notes.QueryAsync(new NoteQuery(Text: "dune"))).Single().Excerpt); // Markdown markers stripped
        Assert.Contains(await _notes.GetTagsAsync(), t => t.Tag == "network" && t.Count == 1);
        Assert.Contains(await _search.SearchAsync(new SearchRequest("version")), h => h.Kind == SearchResultKind.Note && h.Id == note.Id);
        // palette snippets read as text, not Markdown
        var md = await _notes.CreateAsync(new NoteDraft("Lab", "## Cameras\n- **VLAN 30** for the `cams`\n- [ ] label the ports"));
        var snippet = Assert.Single(await _search.SearchAsync(new SearchRequest("cameras")), h => h.Id == md.Id).Snippet!;
        Assert.DoesNotContain("**", snippet);
        Assert.DoesNotContain("##", snippet);
        Assert.DoesNotContain("[ ]", snippet);
        Assert.Contains("VLAN 30", snippet);

        await _notes.SetContextAsync(note.Id, null);
        Assert.Empty((await _projects.GetAsync(project.Id))!.Notes);
        await _notes.DeleteAsync(note.Id);
        Assert.Empty(await _notes.GetRevisionsAsync(note.Id));
    }

    // ───────────── projection, capture, links ─────────────

    [Fact]
    public async Task Calendar_projects_other_modules_and_respects_sources()
    {
        var project = await _projects.CreateAsync(new ProjectDraft("Website", null, ProjectStatus.Active, ProjectPriority.Normal, null, Today.AddDays(3), "Gold", "Projects", "mountains-dusk", "", null));
        await _tasks.CreateAsync(new TaskItemDraft("Publish", DueDate: Today.AddDays(1), DueTime: new TimeOnly(14, 0)));
        var trip = await _travel.CreateTripAsync(new TripDraft("Italy", TripStatus.Upcoming, Today.AddDays(5), Today.AddDays(9)));
        await _travel.EnsureDaysForDatesAsync(trip.Id);
        var day = (await _travel.GetTripAsync(trip.Id))!.Days[1];
        await _travel.AddItemAsync(day.Id, new ItineraryItemDraft("Train to Florence", TimeSlot.Exact, new TimeOnly(8, 15)));
        await _travel.AddItemAsync(day.Id, new ItineraryItemDraft("Gelato", TimeSlot.Afternoon, null));
        await _calendar.CreateEventAsync(new EventDraft("Raid night", Now.AddDays(2).Date.AddHours(20), Now.AddDays(2).Date.AddHours(23), CategoryId: 3));
        await _calendar.CreateEventAsync(new EventDraft("Dentist", Now.Date.AddHours(15), Now.Date.AddHours(16)));

        var from = Now.Date;
        var to = from.AddDays(14);
        var all = await _query.GetItemsAsync(from, to, CalendarSources.All);
        Assert.Contains(all, i => i.Kind == CalendarItemKind.ProjectDeadline && i.Ref == new EntityRef(EntityKind.Project, project.Id) && i.IsAllDay);
        Assert.Contains(all, i => i.Kind == CalendarItemKind.Task && i.Start == from.AddDays(1).AddHours(14) && !i.IsAllDay);
        var tripItem = Assert.Single(all, i => i.Kind == CalendarItemKind.Trip);
        Assert.Equal((from.AddDays(5), from.AddDays(10)), (tripItem.Start, tripItem.End));
        Assert.Equal(new DateOnly(2026, 10, 9), tripItem.EndDate);
        Assert.Equal(from.AddDays(6).AddHours(8.25), Assert.Single(all, i => i.Kind == CalendarItemKind.Itinerary).Start); // only exact times
        Assert.True(all.First().IsAllDay); // all-day items lead

        var noGaming = await _query.GetItemsAsync(from, to, CalendarSources.All & ~CalendarSources.Gaming);
        Assert.DoesNotContain(noGaming, i => i.Title == "Raid night");
        Assert.Contains(noGaming, i => i.Title == "Dentist");
        var onlyTravel = await _query.GetItemsAsync(from, to, CalendarSources.Travel);
        Assert.All(onlyTravel, i => Assert.True(i.Kind is CalendarItemKind.Trip or CalendarItemKind.Itinerary));
    }

    [Fact]
    public async Task Quick_capture_creates_tasks_notes_and_events_from_one_line()
    {
        var capture = new QuickCaptureService(_tasks, _notes, _calendar);
        var t = await capture.CaptureAsync(CaptureKind.Task, "Backup server Friday", Now);
        Assert.Equal(("Backup server", new DateOnly(2026, 10, 2)), (t.Title, t.Date!.Value));
        var e = await capture.CaptureAsync(CaptureKind.Event, "Dentist tomorrow at 9", Now);
        var ev = (await _calendar.GetEventAsync(e.Id))!;
        Assert.Equal((new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 1, 10, 0, 0), false), (ev.Start, ev.End, ev.IsAllDay));
        var n = await capture.CaptureAsync(CaptureKind.Note, "Remember router configuration\nport 8443 is the admin UI", Now);
        var note = (await _notes.GetAsync(n.Id))!;
        Assert.Equal("Remember router configuration", note.Title);
        Assert.Contains("8443", note.Content);
        var bare = await capture.CaptureAsync(CaptureKind.Task, "tomorrow", Now);
        Assert.Equal("tomorrow", bare.Title); // nothing but a date: keep the text
    }

    [Fact]
    public async Task Links_to_deleted_records_are_detached_at_startup()
    {
        var trip = await _travel.CreateTripAsync(new TripDraft("Short-lived"));
        var task = await _tasks.CreateAsync(new TaskItemDraft("Pack", Context: new EntityRef(EntityKind.Trip, trip.Id)));
        var note = await _notes.CreateAsync(new NoteDraft("Ideas", Context: new EntityRef(EntityKind.Trip, trip.Id)));
        var directory = new EntityDirectory(_factory);
        Assert.Equal("Short-lived", (await directory.DescribeAsync([new EntityRef(EntityKind.Trip, trip.Id)]))[new EntityRef(EntityKind.Trip, trip.Id)].Name);
        await _travel.DeleteTripAsync(trip.Id);
        Assert.Empty(await directory.DescribeAsync([new EntityRef(EntityKind.Trip, trip.Id)]));
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        Assert.Null((await _tasks.GetAsync(task.Id))!.Context);
        Assert.Null((await _notes.GetAsync(note.Id))!.Context);
    }
}

public sealed class OrganizerMigrationTests
{
    [Fact]
    public async Task Project_tasks_and_notes_become_arcadia_tasks_and_notes_with_their_ids()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arcadia-p6-{Guid.NewGuid():N}")).FullName; // own folder: the upgrade writes Backups\ next to the file
        var file = Path.Combine(dir, "arcadia.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var db = new ArcadiaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("Travel");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Projects (Id, Name, Status, Priority, Accent, Icon, ArtworkTheme, ArtworkSeed, Tags, ProgressMode, ManualProgress, IsPinned, CreatedAtUtc, UpdatedAtUtc, IsSample) " +
                    "VALUES (4, 'Home Lab', 'Active', 'High', 'Cyan', 'Projects', 'neon-city', 3, '', 'TaskBased', 0, 0, '2026-09-01 10:00:00', '2026-09-01 10:00:00', 0)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO ProjectTasks (Id, ProjectId, ParentTaskId, Title, Status, Priority, DueDate, Tags, SortOrder, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc) VALUES " +
                    "(11, 4, NULL, 'Rack the server', 'InProgress', 'High', '2026-10-01', 'hardware', 1, '2026-09-01 10:00:00', '2026-09-02 10:00:00', NULL), " +
                    "(12, 4, 11, 'Buy rails', 'Completed', 'Normal', NULL, '', 1, '2026-09-01 10:00:00', '2026-09-02 10:00:00', '2026-09-02 10:00:00'), " +
                    "(13, 4, 11, 'Cable it', 'ToDo', 'Low', NULL, '', 2, '2026-09-01 10:00:00', '2026-09-02 10:00:00', NULL)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO ProjectNotes (Id, ProjectId, Title, Content, IsPinned, Tags, CreatedAtUtc, UpdatedAtUtc) VALUES (21, 4, 'IP plan', '10.0.0.0/24 — Netzplan ✓ مخطط', 1, 'network', '2026-09-01 10:00:00', '2026-09-03 10:00:00')");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO SearchIndex (kind, entity_id, project_id, title, body, tags) VALUES ('task', 11, 4, 'Rack the server', '', 'hardware'), ('note', 21, 4, 'IP plan', '10.0.0.0/24 — Netzplan', 'network')");
            }

            var factory = new FileFactory(options);
            await new DatabaseInitializer(factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);

            await using var after = new ArcadiaDbContext(options);
            Assert.Contains(await after.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Organizer", StringComparison.Ordinal));
            var tasks = await after.Tasks.OrderBy(t => t.Id).ToListAsync();
            Assert.Equal(new[] { 11, 12, 13 }, tasks.Select(t => t.Id));
            Assert.All(tasks, t => Assert.Equal(4, t.ProjectId));
            Assert.Equal(new int?[] { null, 11, 11 }, tasks.Select(t => t.ParentTaskId));
            Assert.Equal((TaskItemStatus.InProgress, ProjectPriority.High, new DateOnly(2026, 10, 1), "hardware"), (tasks[0].Status, tasks[0].Priority, tasks[0].DueDate!.Value, tasks[0].Tags));
            Assert.NotNull(tasks[1].CompletedAtUtc);
            var note = await after.Notes.SingleAsync();
            Assert.Equal((21, 4, "IP plan", true), (note.Id, note.ProjectId!.Value, note.Title, note.IsPinned));
            Assert.Equal("10.0.0.0/24 — Netzplan ✓ مخطط", note.Content);
            Assert.Equal(7, await after.CalendarCategories.CountAsync());
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "Backups"), "arcadia-before-*.db")); // named after the newest migration applied

            // the Projects module and search keep working on the moved rows; new tasks get fresh ids
            var projects = new ProjectStore(factory, TimeProvider.System);
            var summary = (await projects.ListAsync()).Single();
            Assert.Equal(2, summary.OpenTasks); // the open task and its open subtask, as in Phase 4
            var search = new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(factory)]);
            Assert.Contains(await search.SearchAsync(new SearchRequest("rack")), h => h.Kind == SearchResultKind.Task && h.Id == 11);
            var created = await new TaskStore(factory, TimeProvider.System).CreateAsync(new TaskItemDraft("Label ports", Context: new EntityRef(EntityKind.Project, 4)));
            Assert.True(created.Id > 13);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FileFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}
