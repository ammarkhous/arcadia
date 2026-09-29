using System.Globalization;
using Arcadia.App.Services;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Organizer;

/// <summary>What the Calendar, Tasks and Notes pages share (one place instead of long constructors).</summary>
public sealed class OrganizerContext(
    ILocalizer loc, IDialogService dialogs, INotificationService notifications, INavigationService navigation, IMessenger messenger,
    IEntityDirectory directory, ISettingsService settings, TimeProvider time, StartupState startup, ILogger<OrganizerContext> logger)
{
    /// <summary>Completes when the database is migrated; pages await it before their first query.</summary>
    public Task Ready => startup.DatabaseReady;

    public ILocalizer Loc { get; } = loc;
    public IDialogService Dialogs { get; } = dialogs;
    public INotificationService Notifications { get; } = notifications;
    public INavigationService Navigation { get; } = navigation;
    public IMessenger Messenger { get; } = messenger;
    public IEntityDirectory Directory { get; } = directory;
    public ISettingsService Settings { get; } = settings;
    public OrganizerSettings Prefs => Settings.Current.Organizer;
    public ILogger Logger { get; } = logger;

    /// <summary>Local wall-clock now (all organizer dates are local, see <see cref="CalendarEvent"/>).</summary>
    public DateTime Now => time.GetLocalNow().DateTime;
    public DateOnly Today => DateOnly.FromDateTime(Now);

    public void Changed(OrganizerChange what) => Messenger.Send(new OrganizerChangedMessage(what));

    public void SavePrefs() => _ = SaveSafelyAsync();

    private async Task SaveSafelyAsync()
    {
        try { await Settings.SaveAsync(); }
        catch (Exception ex) { Logger.LogWarning(ex, "Saving organizer preferences failed"); }
    }

    /// <summary>Runs a store call; on failure shows a friendly message (validation text as is) and returns false.</summary>
    public async Task<bool> TryAsync(Func<Task> action, string errorKey)
    {
        try
        {
            await action();
            return true;
        }
        catch (ArgumentException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
            return false;
        }
        catch (KeyNotFoundException)
        {
            Notifications.Show(Loc["Org_ErrGone"], NotificationKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Organizer action failed ({Key})", errorKey);
            Notifications.Show(Loc[errorKey], NotificationKind.Warning);
            return false;
        }
    }
}

/// <summary>Dates and times the way Arcadia says them (culture-aware; stored values are never formatted strings).</summary>
public static class OrgText
{
    public static string Time(TimeOnly t, CultureInfo c) => t.ToString("t", c);
    public static string Time(DateTime t, CultureInfo c) => t.ToString("t", c);

    /// <summary>"Today", "Tomorrow", "Yesterday", a weekday within the week, otherwise "Fri 9 Oct" (with the year when not this year).</summary>
    public static string Day(DateOnly d, DateOnly today, ILocalizer loc)
    {
        var diff = d.DayNumber - today.DayNumber;
        return diff switch
        {
            0 => loc["Cal_Today"],
            1 => loc["Cal_Tomorrow"],
            -1 => loc["Cal_Yesterday"],
            > 1 and < 7 => d.ToString("dddd", loc.Culture),
            _ => d.Year == today.Year ? d.ToString("ddd d MMM", loc.Culture) : d.ToString("d MMM yyyy", loc.Culture),
        };
    }

    public static string Due(DateOnly? due, TimeOnly? time, DateTime now, ILocalizer loc)
    {
        if (due is not { } d) return string.Empty;
        var today = DateOnly.FromDateTime(now);
        var text = Day(d, today, loc);
        if (time is { } t) text += " · " + Time(t, loc.Culture);
        if (d < today) text = loc.Format("Task_OverdueSince", text);
        return text;
    }

    /// <summary>Red when overdue, amber when due today, steel otherwise (restrained on purpose).</summary>
    public static string DueTone(TaskItemStatus status, DateOnly? due, TimeOnly? time, DateTime now) =>
        TaskRules.IsClosed(status) || due == null ? "Steel"
        : TaskRules.IsOverdue(status, due, time, now) ? "Red"
        : due == DateOnly.FromDateTime(now) ? "Amber" : "Steel";

    /// <summary>Only High and Critical get a mark; Normal and Low stay quiet.</summary>
    public static string? PriorityTone(ProjectPriority p) => p switch
    {
        ProjectPriority.Critical => "Red",
        ProjectPriority.High => "Amber",
        _ => null,
    };

    public static string KindLabel(EntityKind k, ILocalizer loc) => loc["EntityKind_" + k];

    public static bool TryTime(string? text, CultureInfo culture, out TimeOnly? time)
    {
        time = null;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return true;
        if (TimeOnly.TryParse(t, culture, DateTimeStyles.None, out var v) || TimeOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out v))
        {
            time = new TimeOnly(v.Hour, v.Minute);
            return true;
        }
        // "9" or "18" alone: whole hours
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 0 and <= 23)
        {
            time = new TimeOnly(h, 0);
            return true;
        }
        return false;
    }

    public static string TimeInput(TimeOnly? t, CultureInfo c) => t?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>"PROJECT · Arcadia Development": what a task, note or event is about; clicking opens it.</summary>
public sealed partial class ContextChip(EntityInfo info, string kindLabel, INavigationService navigation) : ObservableObject
{
    public EntityRef Ref => info.Ref;
    public string Name => info.Name;
    public string Icon => info.Icon;
    public string Accent => info.Accent;
    public string KindLabel { get; } = kindLabel;
    public override string ToString() => $"{KindLabel} {Name}";

    [RelayCommand] private void Open() => navigation.Open(info.Ref);

    public static async Task<Dictionary<EntityRef, ContextChip>> ResolveAsync(OrganizerContext ctx, IEnumerable<EntityRef> refs)
    {
        var infos = await ctx.Directory.DescribeAsync(refs);
        return infos.ToDictionary(kv => kv.Key, kv => new ContextChip(kv.Value, OrgText.KindLabel(kv.Key.Kind, ctx.Loc), ctx.Navigation));
    }
}
