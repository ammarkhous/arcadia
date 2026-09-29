using System.Collections.ObjectModel;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

[Flags]
public enum OrganizerChange { Events = 1, Tasks = 2, Notes = 4, Finance = 8, All = Events | Tasks | Notes | Finance }

/// <summary>Broadcast whenever events, tasks or notes change, so Today, Home, the calendar and reminders can refresh.</summary>
public sealed record OrganizerChangedMessage(OrganizerChange What);

/// <summary>A reminder the user has been shown (listed under the header bell until dismissed).</summary>
public sealed partial class ReminderNotice(ReminderDue due, string whenText, bool missed) : ObservableObject
{
    public ReminderDue Due { get; } = due;
    public string Title => Due.Title;
    public string? Location => Due.Location;
    public string WhenText { get; } = whenText;
    public bool IsMissed { get; } = missed;
    public string Icon => Due.Source == ReminderSource.Task ? "Tasks" : "Calendar";
    public EntityRef Target => new(Due.Source == ReminderSource.Task ? EntityKind.Task : EntityKind.Event, Due.Id);
    public override string ToString() => $"{Title} — {WhenText}";
}

/// <summary>Reminders shown this session, for the header bell (newest first).</summary>
public sealed partial class NotificationCenter : ObservableObject
{
    public ObservableCollection<ReminderNotice> Items { get; } = [];
    [ObservableProperty] private int _unread;

    public void Add(ReminderNotice notice)
    {
        if (Items.Any(n => n.Due.Key == notice.Due.Key)) return;
        Items.Insert(0, notice);
        while (Items.Count > 50) Items.RemoveAt(Items.Count - 1);
        Unread++;
    }

    public void Remove(ReminderNotice notice)
    {
        Items.Remove(notice);
        Unread = Math.Min(Unread, Items.Count);
    }

    public void MarkRead() => Unread = 0;

    public void Clear()
    {
        Items.Clear();
        Unread = 0;
    }
}

/// <summary>Shows reminders (the Arcadia reminder card, the bell list, an optional sound). Implemented by the UI.</summary>
public interface IReminderPresenter
{
    void Present(IReadOnlyList<ReminderNotice> notices);
}

/// <summary>
/// Local reminders while Arcadia runs. There is no polling: after every check the service works out the next reminder
/// (events expanded per occurrence, tasks by their reminder time) and arms one timer for that moment, capped at six
/// hours so clock changes and new days are picked up. Data changes, Windows resume and clock changes re-plan
/// immediately. What was shown is remembered (<see cref="OrganizerSettings.RemindersShownUntil"/>), so a restart never
/// repeats a reminder; reminders missed while Arcadia was closed (up to a day) are shown once, marked as missed.
/// A background Windows service is not needed for this and is intentionally not installed.
/// </summary>
public sealed class ReminderService : IDisposable
{
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(6);
    public static readonly TimeSpan MissedLookback = TimeSpan.FromDays(1);
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(2);
    private static readonly TimeSpan MaxLead = TimeSpan.FromMinutes(Arcadia.Data.Organizer.CalendarStore.MaxReminderMinutes);

    private readonly ICalendarStore _calendar;
    private readonly ITaskQueryService _tasks;
    private readonly ISettingsService _settings;
    private readonly IReminderPresenter _presenter;
    private readonly ILocalizer _loc;
    private readonly TimeProvider _time;
    private readonly ILogger<ReminderService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private bool _started;
    private bool _disposed;
    private int _failures;

    public ReminderService(ICalendarStore calendar, ITaskQueryService tasks, ISettingsService settings, IReminderPresenter presenter, ILocalizer loc,
        IMessenger messenger, TimeProvider time, ILogger<ReminderService> logger)
    {
        _calendar = calendar;
        _tasks = tasks;
        _settings = settings;
        _presenter = presenter;
        _loc = loc;
        _time = time;
        _logger = logger;
        messenger.Register<ReminderService, OrganizerChangedMessage>(this, (r, m) => { if ((m.What & (OrganizerChange.Events | OrganizerChange.Tasks)) != 0) r.Reschedule(); });
    }

    /// <summary>The next moment the service will look (for diagnostics and tests).</summary>
    public DateTime? NextCheck { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Microsoft.Win32.SystemEvents.TimeChanged += OnSystemChange;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Reschedule();
    }

    private void OnSystemChange(object? sender, EventArgs e) => Reschedule();

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Reschedule();
    }

    /// <summary>Checks now (showing what is due) and arms the timer for the next reminder.</summary>
    public void Reschedule()
    {
        if (!_started || _disposed) return;
        _ = Task.Run(RunAsync);
    }

    /// <summary>Shows the reminder again after a while (kept in memory only; dismissing ends it).</summary>
    public void Snooze(ReminderNotice notice, TimeSpan delay)
    {
        var at = _time.GetLocalNow().DateTime + delay;
        ITimer? t = null;
        t = _time.CreateTimer(_ =>
        {
            try { _presenter.Present([new ReminderNotice(notice.Due with { TriggerAt = at }, notice.WhenText, false)]); }
            catch (Exception ex) { _logger.LogWarning(ex, "Showing a snoozed reminder failed"); }
            t?.Dispose();
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    private async Task RunAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            // a check is running; it re-plans at the end anyway, but data may have changed since: run once more afterwards
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        try
        {
            var now = _time.GetLocalNow().DateTime;
            var org = _settings.Current.Organizer;
            var shownUntil = org.RemindersShownUntil ?? now;
            if (shownUntil > now) shownUntil = now; // the clock went back
            if (now - shownUntil > MissedLookback) shownUntil = now - MissedLookback;

            var due = await PlanAsync(shownUntil, now + Horizon).ConfigureAwait(false);
            var toShow = due.Where(d => d.TriggerAt <= now).ToList();
            if (toShow.Count > 0)
            {
                var notices = toShow.Select(d => new ReminderNotice(d, WhenText(d, now), now - d.TriggerAt > TimeSpan.FromMinutes(2))).ToList();
                try { _presenter.Present(notices); }
                catch (Exception ex) { _logger.LogWarning(ex, "Showing {Count} reminder(s) failed", notices.Count); } // not retried: they stay in the bell list
            }
            if (org.RemindersShownUntil != now)
            {
                org.RemindersShownUntil = now;
                await _settings.SaveAsync().ConfigureAwait(false);
            }

            var next = due.Where(d => d.TriggerAt > now).Select(d => (DateTime?)d.TriggerAt).FirstOrDefault();
            Arm(now, next);
            _failures = 0;
        }
        catch (Exception ex)
        {
            // e.g. the database is busy or gone: log once per streak, try again later (never in a tight loop)
            if (_failures++ == 0) _logger.LogWarning(ex, "Reminder check failed; retrying in a few minutes");
            Arm(_time.GetLocalNow().DateTime, _time.GetLocalNow().DateTime + TimeSpan.FromMinutes(Math.Min(60, 5 * _failures)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ReminderDue>> PlanAsync(DateTime from, DateTime to)
    {
        var events = await _calendar.GetEventsForRangeAsync(from, to + MaxLead).ConfigureAwait(false);
        var tasks = await _tasks.GetRemindersAsync(from, to).ConfigureAwait(false);
        return ReminderPlanner.Due(events, tasks, from, to);
    }

    /// <summary>How long to sleep until <paramref name="next"/> (at least a second, at most <see cref="MaxSleep"/>).</summary>
    public static TimeSpan SleepFor(DateTime now, DateTime? next)
    {
        var wait = next is { } n ? n - now : MaxSleep;
        if (wait > MaxSleep) wait = MaxSleep;
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    private void Arm(DateTime now, DateTime? next)
    {
        if (_disposed) return;
        var wait = SleepFor(now, next);
        NextCheck = now + wait;
        _timer?.Dispose();
        _timer = _time.CreateTimer(_ => Reschedule(), null, wait, Timeout.InfiniteTimeSpan);
    }

    private string WhenText(ReminderDue d, DateTime now)
    {
        if (d.StartsAt is not { } start) return _loc["Rem_TaskReminder"];
        var culture = _loc.Culture;
        var day = DateOnly.FromDateTime(start);
        var today = DateOnly.FromDateTime(now);
        var dayText = day == today ? _loc["Cal_Today"] : day == today.AddDays(1) ? _loc["Cal_Tomorrow"] : start.ToString("ddd d MMM", culture);
        // all-day occurrences start at midnight: the day is enough
        return start.TimeOfDay == TimeSpan.Zero ? dayText : $"{dayText} · {start.ToString("t", culture)}";
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        if (_started)
        {
            Microsoft.Win32.SystemEvents.TimeChanged -= OnSystemChange;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }
}
