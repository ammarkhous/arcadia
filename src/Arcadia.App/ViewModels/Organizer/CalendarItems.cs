using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcadia.App.ViewModels.Organizer;

public enum CalendarView { Today, Month, Week, Day, Agenda }

public sealed partial class CalendarViewOption(CalendarView view, string label, string icon, string shortcut) : ObservableObject
{
    public CalendarView View { get; } = view;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public string Shortcut { get; } = shortcut;
    [ObservableProperty] private bool _isSelected;
    public override string ToString() => Label;
}

/// <summary>A projected calendar entry as the views show it.</summary>
public sealed partial class CalendarEntry : ObservableObject
{
    public CalendarEntry(CalendarItem item, ILocalizer loc, ContextChip? context)
    {
        Item = item;
        Context = context;
        var c = loc.Culture;
        TimeText = item.IsAllDay ? loc["Cal_AllDay"]
            : item.Kind == CalendarItemKind.Task ? OrgText.Time(item.Start, c)
            : item.End > item.Start && item.End.Date == item.Start.Date ? $"{OrgText.Time(item.Start, c)} – {OrgText.Time(item.End, c)}"
            : OrgText.Time(item.Start, c);
        StartText = item.IsAllDay ? string.Empty : OrgText.Time(item.Start, c);
        KindLabel = item.Kind switch
        {
            CalendarItemKind.Event => loc["CalKind_Event"],
            CalendarItemKind.Task => loc["CalKind_Task"],
            CalendarItemKind.ProjectDeadline => loc["CalKind_Deadline"],
            CalendarItemKind.Trip => loc["CalKind_Trip"],
            _ => loc["CalKind_Itinerary"],
        };
        _isDone = item.IsDone;
    }

    public CalendarItem Item { get; }
    public ContextChip? Context { get; }
    public string Title => Item.Title;
    public string Accent => Item.Accent;
    public string Icon => Item.Icon;
    public string TimeText { get; }
    public string StartText { get; }
    public string KindLabel { get; }
    public string? Location => Item.Location;
    public string? Subtitle => Item.Subtitle;
    public bool IsAllDay => Item.IsAllDay;
    public bool IsRecurring => Item.IsRecurring;
    public bool HasReminder => Item.ReminderMinutes != null;
    public bool IsTask => Item.Kind == CalendarItemKind.Task;
    public bool IsEvent => Item.Kind == CalendarItemKind.Event;
    [ObservableProperty] private bool _isDone;
    /// <summary>Already over (Today's timeline dims it).</summary>
    [ObservableProperty] private bool _isPast;
    public override string ToString() => $"{TimeText} {Title}";
}

/// <summary>One cell of the month grid.</summary>
public sealed partial class MonthDay(DateOnly date, bool inMonth, bool isToday, bool isWeekend) : ObservableObject
{
    public const int VisibleEntries = 3;
    public DateOnly Date { get; } = date;
    public string DayNumber => Date.Day.ToString(CultureInfo.CurrentCulture);
    public bool InMonth { get; } = inMonth;
    public bool IsToday { get; } = isToday;
    public bool IsWeekend { get; } = isWeekend;
    public ObservableCollection<CalendarEntry> Entries { get; } = [];
    [ObservableProperty] private string? _moreText;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasTasks;
    public override string ToString() => Date.ToString("D", CultureInfo.CurrentCulture);
}

/// <summary>A timed block in the week/day grid, positioned by minutes and side-by-side lanes for overlaps.</summary>
public sealed class TimedBlock(CalendarEntry entry, double startMinutes, double durationMinutes, int lane, int lanes)
{
    public CalendarEntry Entry { get; } = entry;
    public double StartMinutes { get; } = startMinutes;
    public double DurationMinutes { get; } = durationMinutes;
    public int Lane { get; } = lane;
    public int Lanes { get; } = lanes;
    public override string ToString() => Entry.ToString();

    /// <summary>Greedy lane assignment: overlapping blocks share the width; each cluster of overlaps uses as many lanes as it needs.</summary>
    public static List<TimedBlock> Layout(IEnumerable<(CalendarEntry Entry, double Start, double Duration)> items)
    {
        var sorted = items.OrderBy(i => i.Start).ThenByDescending(i => i.Duration).ToList();
        var result = new List<TimedBlock>();
        var cluster = new List<(CalendarEntry Entry, double Start, double Duration, int Lane)>();
        var laneEnds = new List<double>();
        double clusterEnd = double.MinValue;
        void Flush()
        {
            var lanes = laneEnds.Count;
            result.AddRange(cluster.Select(c => new TimedBlock(c.Entry, c.Start, c.Duration, c.Lane, Math.Max(1, lanes))));
            cluster.Clear();
            laneEnds.Clear();
        }
        foreach (var (entry, start, duration) in sorted)
        {
            var visible = Math.Max(duration, 22); // short items still need room for their title
            if (start >= clusterEnd && cluster.Count > 0) Flush();
            var lane = laneEnds.FindIndex(end => end <= start);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(start + visible); }
            else laneEnds[lane] = start + visible;
            cluster.Add((entry, start, duration, lane));
            clusterEnd = Math.Max(clusterEnd == double.MinValue ? start : clusterEnd, start + visible);
        }
        if (cluster.Count > 0) Flush();
        return result;
    }
}

public sealed partial class DayColumn(DateOnly date, string header, string dayNumber, bool isToday, bool isWeekend) : ObservableObject
{
    public DateOnly Date { get; } = date;
    public string Header { get; } = header;
    public string DayNumber { get; } = dayNumber;
    public bool IsToday { get; } = isToday;
    public bool IsWeekend { get; } = isWeekend;
    public ObservableCollection<CalendarEntry> AllDay { get; } = [];
    public ObservableCollection<TimedBlock> Timed { get; } = [];
    [ObservableProperty] private double _nowMinutes = -1;
    public override string ToString() => Date.ToString("D", CultureInfo.CurrentCulture);
}

public sealed class AgendaGroup(string title, string subtitle, bool isToday, IReadOnlyList<CalendarEntry> entries)
{
    public bool IsEmpty => Entries.Count == 0;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public bool IsToday { get; } = isToday;
    public IReadOnlyList<CalendarEntry> Entries { get; } = entries;
    public override string ToString() => $"{Title} {Subtitle}";
}

public sealed partial class SourceToggle(CalendarSources source, string label, string icon, string accent, bool on) : ObservableObject
{
    public CalendarSources Source { get; } = source;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public string Accent { get; } = accent;
    [ObservableProperty] private bool _isOn = on;
    public override string ToString() => Label;
}

public sealed record CategoryRow(CalendarCategory Category, string Name)
{
    public string Icon => Category.Icon;
    public string Accent => Category.Accent;
    public override string ToString() => Name;
}
