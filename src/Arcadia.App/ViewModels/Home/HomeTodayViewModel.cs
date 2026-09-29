using Arcadia.App.Services;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.Core.Organizer;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Home;

/// <summary>
/// Home's compact "Today" strip: the date and next event, tasks due, the next deadline and a pinned note. It sits
/// between the featured game and the game panels without changing Home's composition; each cell can be hidden in
/// Settings (all hidden = no strip). Loaded on its own, so the game dashboard never waits for it.
/// </summary>
public sealed partial class HomeTodayViewModel : ObservableObject, IRecipient<OrganizerChangedMessage>
{
    private readonly ICalendarQueryService _calendar;
    private readonly ITaskQueryService _tasks;
    private readonly INoteQueryService _notes;
    private readonly OrganizerContext _ctx;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _cts;

    public HomeTodayViewModel(ICalendarQueryService calendar, ITaskQueryService tasks, INoteQueryService notes, OrganizerContext ctx, ISettingsService settings)
    {
        _calendar = calendar;
        _tasks = tasks;
        _notes = notes;
        _ctx = ctx;
        _settings = settings;
        ctx.Messenger.Register(this);
        settings.SettingsChanged += (_, _) => ApplyVisibility();
        ApplyVisibility();
    }

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _showToday;
    [ObservableProperty] private bool _showTasks;
    [ObservableProperty] private bool _showDeadline;
    [ObservableProperty] private bool _showPinnedNote;

    [ObservableProperty] private string _dayName = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;
    [ObservableProperty] private string? _nextTitle;
    [ObservableProperty] private string? _nextWhen;
    [ObservableProperty] private string _agendaText = string.Empty;
    [ObservableProperty] private string _tasksCount = "0";
    [ObservableProperty] private string _tasksText = string.Empty;
    [ObservableProperty] private string? _tasksWarning;
    [ObservableProperty] private IReadOnlyList<string> _taskTitles = [];
    [ObservableProperty] private string? _deadlineTitle;
    [ObservableProperty] private string? _deadlineWhen;
    [ObservableProperty] private string _deadlineIcon = "Flag";
    [ObservableProperty] private string _deadlineAccent = "Cyan";
    [ObservableProperty] private string? _noteTitle;
    [ObservableProperty] private string? _noteExcerpt;
    private CalendarItem? _deadline;
    private NoteRow? _note;
    private CalendarItem? _next;

    private void ApplyVisibility()
    {
        var h = _settings.Current.Home;
        ShowToday = h.ShowToday;
        ShowTasks = h.ShowTasks;
        ShowDeadline = h.ShowDeadline;
        ShowPinnedNote = h.ShowPinnedNote;
        IsVisible = ShowToday || ShowTasks || ShowDeadline || ShowPinnedNote;
    }

    public void Receive(OrganizerChangedMessage message) => _ = LoadAsync();

    partial void OnIsVisibleChanged(bool value)
    {
        if (value) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (!IsVisible) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await _ctx.Ready;
            var loc = _ctx.Loc;
            var now = _ctx.Now;
            var today = _ctx.Today;
            DayName = today.ToString("dddd", loc.Culture);
            DateText = today.ToString("M", loc.Culture);

            // SQLite's "async" calls complete synchronously: gather everything on a worker so Home paints at once
            var sources = SourcesFromPrefs();
            var token = cts.Token;
            var (day, counts, taskTitles, ahead, note) = await Task.Run(async () =>
            (
                await _calendar.GetItemsAsync(now.Date, now.Date.AddDays(1), sources, token),
                await _tasks.GetCountsAsync(now, token),
                (await _tasks.QueryAsync(new TaskQuery(TaskView.Today, Take: 2), now, token)).Select(t => t.Title).ToList(),
                await _calendar.GetItemsAsync(now.Date.AddDays(1), now.Date.AddDays(120), CalendarSources.Projects | CalendarSources.Travel, token),
                (await _notes.QueryAsync(new NoteQuery(NoteView.Pinned, Take: 1), token)).FirstOrDefault()
            ), token);
            if (token.IsCancellationRequested) return;

            _next = day.Where(i => !i.IsAllDay && i.Start >= now && !i.IsDone).OrderBy(i => i.Start).FirstOrDefault();
            NextTitle = _next?.Title;
            NextWhen = _next == null ? null : OrgText.Time(_next.Start, loc.Culture);
            AgendaText = day.Count == 0 ? loc["HomeToday_Clear"] : loc.Format("HomeToday_Agenda", day.Count);

            TasksCount = (counts.Today + counts.Overdue).ToString(loc.Culture);
            TasksText = loc["HomeToday_TasksDue"];
            TasksWarning = counts.Overdue > 0 ? loc.Format("HomeToday_Overdue", counts.Overdue) : null;
            TaskTitles = taskTitles;

            _deadline = ahead.Where(i => i.StartDate > today && i.Kind is CalendarItemKind.ProjectDeadline or CalendarItemKind.Trip).OrderBy(i => i.Start).FirstOrDefault();
            DeadlineTitle = _deadline?.Title;
            DeadlineWhen = _deadline == null ? null : OrgText.Day(_deadline.StartDate, today, loc) + " · " + loc.Format("HomeToday_InDays", _deadline.StartDate.DayNumber - today.DayNumber);
            DeadlineIcon = _deadline?.Icon ?? "Flag";
            DeadlineAccent = _deadline?.Accent ?? "Cyan";

            _note = note;
            NoteTitle = _note?.Title;
            NoteExcerpt = _note?.Excerpt;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Home's Today strip could not be loaded");
        }
    }

    private CalendarSources SourcesFromPrefs()
    {
        var p = _ctx.Prefs;
        var s = CalendarSources.None;
        if (p.ShowEvents) s |= CalendarSources.Events;
        if (p.ShowTasks) s |= CalendarSources.Tasks;
        if (p.ShowProjects) s |= CalendarSources.Projects;
        if (p.ShowTravel) s |= CalendarSources.Travel;
        if (p.ShowGaming) s |= CalendarSources.Gaming;
        return s;
    }

    [RelayCommand] private void OpenToday() => _ctx.Navigation.Navigate(Sections.Calendar);
    [RelayCommand] private void OpenNext() { if (_next != null) _ctx.Navigation.Open(_next.Ref); else OpenToday(); }
    [RelayCommand] private void OpenTasks() => _ctx.Navigation.Navigate(Sections.Tasks);
    [RelayCommand] private void OpenDeadline() { if (_deadline != null) _ctx.Navigation.Open(_deadline.Ref); }
    [RelayCommand] private void OpenNote() { if (_note != null) _ctx.Navigation.Open(new EntityRef(EntityKind.Note, _note.Id)); else _ctx.Navigation.Navigate(Sections.Notes); }
}
