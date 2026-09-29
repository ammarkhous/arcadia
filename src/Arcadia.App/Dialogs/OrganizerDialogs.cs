using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

public sealed partial class KindChoice(EntityKind kind, string label, string icon) : ObservableObject
{
    public EntityKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    [ObservableProperty] private bool _isSelected;
    public override string ToString() => Label;
}

/// <summary>Link a task, note or event to something in Arcadia: pick the kind, filter, choose.</summary>
public sealed partial class EntityPickerViewModel : DialogViewModel
{
    private readonly OrganizerContext _ctx;
    private CancellationTokenSource? _cts;

    public EntityPickerViewModel(OrganizerContext ctx, IReadOnlyList<EntityKind> kinds)
    {
        _ctx = ctx;
        Title = ctx.Loc["Org_LinkTitle"];
        Kinds = kinds.Select(k => new KindChoice(k, ctx.Loc["EntityKind_" + k], Arcadia.Data.Organizer.EntityDirectory.IconOf(k))).ToList();
        Kinds[0].IsSelected = true;
        _kind = Kinds[0];
    }

    public override string Icon => "Link";
    public override double DialogWidth => 560;
    public IReadOnlyList<KindChoice> Kinds { get; }
    public ObservableCollection<EntityInfo> Items { get; } = [];
    [ObservableProperty] private KindChoice _kind;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private EntityInfo? _selected;
    [ObservableProperty] private bool _isEmpty;

    public override Task OnShownAsync() => LoadAsync();

    partial void OnFilterChanged(string value) => _ = LoadAsync();

    [RelayCommand]
    private async Task ChooseKindAsync(KindChoice? k)
    {
        if (k == null) return;
        foreach (var c in Kinds) c.IsSelected = c == k;
        Kind = k;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token);
            var rows = await _ctx.Directory.FindAsync(Kind.Kind, Filter, 200, cts.Token);
            if (cts.IsCancellationRequested) return;
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            IsEmpty = Items.Count == 0;
            Selected = Items.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (Selected != null) Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>"This event / this and following / all events" for a change to one occurrence of a repeating event.</summary>
public sealed partial class RecurrenceScopeViewModel : DialogViewModel
{
    public RecurrenceScopeViewModel(ILocalizer loc, bool deleting, bool isFirst)
    {
        Title = loc[deleting ? "Evt_ScopeDeleteTitle" : "Evt_ScopeEditTitle"];
        Message = loc[deleting ? "Evt_ScopeDeleteBody" : "Evt_ScopeEditBody"];
        ShowFuture = !isFirst;
    }

    public override string Icon => "Repeat";
    public override double DialogWidth => 460;
    public string Message { get; }
    public bool ShowFuture { get; }
    public RecurrenceScope? Scope { get; private set; }

    [RelayCommand] private void This() { Scope = RecurrenceScope.ThisEvent; Close(true); }
    [RelayCommand] private void Future() { Scope = RecurrenceScope.ThisAndFuture; Close(true); }
    [RelayCommand] private void All() { Scope = RecurrenceScope.AllEvents; Close(true); }
    [RelayCommand] private void Cancel() => Close(false);
}

public enum RepeatChoice { None, Daily, Weekdays, Weekly, Monthly, Yearly, Custom }

public sealed partial class WeekdayToggle(WeekdayMask day, string label) : ObservableObject
{
    public WeekdayMask Day { get; } = day;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isOn;
    public override string ToString() => Label;
}

/// <summary>
/// Create or edit an event. Only the title and the date are needed; time, reminder, repetition, category, location,
/// a saved place and a link to something in Arcadia are optional. Occurrence edits show that occurrence's dates.
/// </summary>
public sealed partial class EventEditorViewModel : DialogViewModel
{
    private readonly OrganizerContext _ctx;
    private readonly CultureInfo _c;

    public EventEditorViewModel(OrganizerContext ctx, IReadOnlyList<CalendarCategory> categories, CalendarEvent? existing, DateTime start, DateTime end, bool allDay,
        Occurrence? occurrence = null, IReadOnlyDictionary<EntityRef, ContextChip>? chips = null)
    {
        _ctx = ctx;
        _c = ctx.Loc.Culture;
        var loc = ctx.Loc;
        IsNew = existing == null;
        IsOccurrence = occurrence != null && existing?.IsRecurring == true;
        Title = loc[IsNew ? "Evt_NewTitle" : "Evt_EditTitle"];
        CategoryOptions = [new Option<int?>(null, loc["Evt_NoCategory"]), .. categories.Select(c => new Option<int?>(c.Id, CategoryName(c, loc)))];
        ReminderOptions =
        [
            new(null, loc["Rem_None"]), new(0, loc["Rem_AtStart"]), new(5, loc.Format("Rem_Minutes", 5)), new(15, loc.Format("Rem_Minutes", 15)),
            new(30, loc.Format("Rem_Minutes", 30)), new(60, loc["Rem_Hour"]), new(120, loc.Format("Rem_Hours", 2)), new(1440, loc["Rem_Day"]), new(10080, loc["Rem_Week"]),
        ];
        RepeatOptions = Enum.GetValues<RepeatChoice>().Select(r => new Option<RepeatChoice>(r, loc["Repeat_" + r])).ToList();
        var dayNames = _c.DateTimeFormat.AbbreviatedDayNames;
        Weekdays = [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new WeekdayToggle(Recurrence.MaskOf(d), dayNames[(int)d]))];

        var s = occurrence?.Start ?? existing?.Start ?? start;
        var e = occurrence?.End ?? existing?.End ?? end;
        var isAllDay = existing?.IsAllDay ?? allDay;
        if (isAllDay && occurrence != null) e = s + Recurrence.Duration(existing!) - TimeSpan.FromDays(1);
        _eventTitle = occurrence?.Title ?? existing?.Title ?? string.Empty;
        _isAllDay = isAllDay;
        _startDate = DateText.Format(DateOnly.FromDateTime(s), _c);
        _startTime = isAllDay ? "09:00" : s.ToString("HH:mm", CultureInfo.InvariantCulture);
        _endDate = DateText.Format(DateOnly.FromDateTime(e), _c);
        _endTime = isAllDay ? "10:00" : e.ToString("HH:mm", CultureInfo.InvariantCulture);
        _location = occurrence?.Location ?? existing?.Location ?? string.Empty;
        _description = occurrence?.Description ?? existing?.Description ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;
        _tags = existing?.Tags ?? string.Empty;
        _category = CategoryOptions.FirstOrDefault(o => o.Value == existing?.CategoryId) ?? CategoryOptions[0];
        var reminder = existing != null ? existing.ReminderMinutes : isAllDay ? null : ctx.Prefs.DefaultReminderMinutes;
        _reminder = ReminderOptions.FirstOrDefault(o => o.Value == reminder) ?? ReminderOptions[0];
        _customReminder = reminder is { } m && ReminderOptions.All(o => o.Value != m) ? m.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _placeId = existing?.PlaceId;
        _context = existing?.Context is { } r && chips != null ? chips.GetValueOrDefault(r) : null;
        _interval = (existing?.Interval ?? 1).ToString(CultureInfo.InvariantCulture);
        _until = DateText.Format(existing?.RecursUntil, _c);
        var repeat = existing == null ? RepeatChoice.None : existing.Frequency switch
        {
            RecurrenceFrequency.Daily => existing.Interval == 1 ? RepeatChoice.Daily : RepeatChoice.Custom,
            RecurrenceFrequency.Weekly when existing.Weekdays == WeekdayMask.Weekdays && existing.Interval == 1 => RepeatChoice.Weekdays,
            RecurrenceFrequency.Weekly => existing.Interval == 1 ? RepeatChoice.Weekly : RepeatChoice.Custom,
            RecurrenceFrequency.Monthly => existing.Interval == 1 ? RepeatChoice.Monthly : RepeatChoice.Custom,
            RecurrenceFrequency.Yearly => existing.Interval == 1 ? RepeatChoice.Yearly : RepeatChoice.Custom,
            _ => RepeatChoice.None,
        };
        _repeat = RepeatOptions.First(o => o.Value == repeat);
        CustomUnits = [new(RecurrenceFrequency.Daily, loc["RepeatUnit_Days"]), new(RecurrenceFrequency.Weekly, loc["RepeatUnit_Weeks"]),
            new(RecurrenceFrequency.Monthly, loc["RepeatUnit_Months"]), new(RecurrenceFrequency.Yearly, loc["RepeatUnit_Years"])];
        _customUnit = CustomUnits.FirstOrDefault(u => u.Value == existing?.Frequency) ?? CustomUnits[1];
        var mask = existing?.Weekdays is { } w && w != WeekdayMask.None ? w : Recurrence.MaskOf(s.DayOfWeek);
        foreach (var d in Weekdays) d.IsOn = (mask & d.Day) != 0;
    }

    public static string CategoryName(CalendarCategory c, ILocalizer loc) => c.Name ?? (c.Key != null ? loc["EvtCat_" + c.Key] : "—");

    public override string Icon => "Calendar";
    public override double DialogWidth => 600;
    public bool IsNew { get; }
    /// <summary>Editing one occurrence of a series (repetition settings are then changed for the whole series only).</summary>
    public bool IsOccurrence { get; }
    public string ConfirmText => _ctx.Loc[IsNew ? "Evt_Create" : "Editor_Save"];
    public IReadOnlyList<Option<int?>> CategoryOptions { get; }
    public IReadOnlyList<Option<int?>> ReminderOptions { get; }
    public IReadOnlyList<Option<RepeatChoice>> RepeatOptions { get; }
    public IReadOnlyList<Option<RecurrenceFrequency>> CustomUnits { get; }
    public IReadOnlyList<WeekdayToggle> Weekdays { get; }

    [ObservableProperty] private string _eventTitle;
    [ObservableProperty] private bool _isAllDay;
    [ObservableProperty] private string _startDate;
    [ObservableProperty] private string _startTime;
    [ObservableProperty] private string _endDate;
    [ObservableProperty] private string _endTime;
    [ObservableProperty] private string _location;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private Option<int?> _category;
    [ObservableProperty] private Option<int?> _reminder;
    [ObservableProperty] private string _customReminder;
    [ObservableProperty] private Option<RepeatChoice> _repeat;
    [ObservableProperty] private Option<RecurrenceFrequency> _customUnit;
    [ObservableProperty] private string _interval;
    [ObservableProperty] private string _until;
    [ObservableProperty] private int? _placeId;
    [ObservableProperty] private string? _placeName;
    [ObservableProperty] private ContextChip? _context;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _showMore;

    public bool ShowWeekdays => Repeat.Value is RepeatChoice.Weekly || (Repeat.Value == RepeatChoice.Custom && CustomUnit.Value == RecurrenceFrequency.Weekly);
    public bool ShowCustom => Repeat.Value == RepeatChoice.Custom;
    public bool Repeats => Repeat.Value != RepeatChoice.None;

    partial void OnRepeatChanged(Option<RepeatChoice> value) { OnPropertyChanged(nameof(ShowWeekdays)); OnPropertyChanged(nameof(ShowCustom)); OnPropertyChanged(nameof(Repeats)); }
    partial void OnCustomUnitChanged(Option<RecurrenceFrequency> value) => OnPropertyChanged(nameof(ShowWeekdays));

    public EventDraft? Draft { get; private set; }
    public bool DeleteRequested { get; private set; }

    public override async Task OnShownAsync()
    {
        if (PlaceId is { } id && (await _ctx.Directory.DescribeAsync([new EntityRef(EntityKind.Place, id)])).TryGetValue(new EntityRef(EntityKind.Place, id), out var info))
            PlaceName = info.Name;
    }

    [RelayCommand]
    private void PickPlace()
    {
        var picker = new EntityPickerViewModel(_ctx, [EntityKind.Place]);
        if (!_ctx.Dialogs.Show(picker) || picker.Selected is not { } info) return;
        PlaceId = info.Ref.Id;
        PlaceName = info.Name;
        if (string.IsNullOrWhiteSpace(Location)) Location = info.Name;
    }

    [RelayCommand]
    private void ClearPlace()
    {
        PlaceId = null;
        PlaceName = null;
    }

    [RelayCommand]
    private void PickContext()
    {
        var picker = new EntityPickerViewModel(_ctx, [EntityKind.Project, EntityKind.Trip, EntityKind.Game, EntityKind.Diagnostic, EntityKind.Document]);
        if (_ctx.Dialogs.Show(picker) && picker.Selected is { } info)
            Context = new ContextChip(info, OrgText.KindLabel(info.Ref.Kind, _ctx.Loc), _ctx.Navigation);
    }

    [RelayCommand] private void ClearContext() => Context = null;

    [RelayCommand]
    private void Confirm()
    {
        var loc = _ctx.Loc;
        Error = null;
        if (string.IsNullOrWhiteSpace(EventTitle)) { Error = loc["Evt_ErrTitle"]; return; }
        if (!DateText.TryParse(StartDate, _c, out var sd) || sd == null || !DateText.TryParse(EndDate, _c, out var ed)) { Error = loc["Proj_ErrDate"]; return; }
        var endDate = ed ?? sd.Value;
        DateTime start, end;
        if (IsAllDay)
        {
            start = sd.Value.ToDateTime(TimeOnly.MinValue);
            end = endDate.ToDateTime(TimeOnly.MinValue);
        }
        else
        {
            if (!OrgText.TryTime(StartTime, _c, out var st) || st == null || !OrgText.TryTime(EndTime, _c, out var et)) { Error = loc["Org_ErrTime"]; return; }
            start = sd.Value.ToDateTime(st.Value);
            end = endDate.ToDateTime(et ?? st.Value.AddHours(1));
            if (ed == null && end <= start) end = start.AddHours(1);
        }
        if (end < start) { Error = loc["Evt_ErrEndBeforeStart"]; return; }

        int? reminder = Reminder.Value;
        if (!string.IsNullOrWhiteSpace(CustomReminder))
        {
            if (!int.TryParse(CustomReminder.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > Arcadia.Data.Organizer.CalendarStore.MaxReminderMinutes)
            { Error = loc["Evt_ErrReminder"]; return; }
            reminder = m;
        }

        var frequency = RecurrenceFrequency.None;
        var interval = 1;
        var mask = WeekdayMask.None;
        DateOnly? until = null;
        switch (Repeat.Value)
        {
            case RepeatChoice.Daily: frequency = RecurrenceFrequency.Daily; break;
            case RepeatChoice.Weekdays: frequency = RecurrenceFrequency.Weekly; mask = WeekdayMask.Weekdays; break;
            case RepeatChoice.Weekly: frequency = RecurrenceFrequency.Weekly; break;
            case RepeatChoice.Monthly: frequency = RecurrenceFrequency.Monthly; break;
            case RepeatChoice.Yearly: frequency = RecurrenceFrequency.Yearly; break;
            case RepeatChoice.Custom:
                frequency = CustomUnit.Value;
                if (!int.TryParse(Interval.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval is < 1 or > 999) { Error = loc["Evt_ErrInterval"]; return; }
                break;
        }
        if (frequency == RecurrenceFrequency.Weekly && mask == WeekdayMask.None)
        {
            foreach (var d in Weekdays.Where(d => d.IsOn)) mask |= d.Day;
            if (mask == WeekdayMask.None) mask = Recurrence.MaskOf(start.DayOfWeek);
        }
        if (frequency != RecurrenceFrequency.None)
        {
            if (!DateText.TryParse(Until, _c, out until)) { Error = loc["Proj_ErrDate"]; return; }
            if (until is { } u && u < DateOnly.FromDateTime(start)) { Error = loc["Evt_ErrUntil"]; return; }
        }

        Draft = new EventDraft(EventTitle.Trim(), start, end, IsAllDay, Location, PlaceId, Category.Value, reminder, frequency, interval, mask, until,
            Description, Notes, Tags, Context?.Ref);
        Close(true);
    }

    [RelayCommand]
    private void Delete()
    {
        DeleteRequested = true;
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Create or edit a calendar category: a name, an icon and one of Arcadia's accent tones.</summary>
public sealed partial class CategoryEditorViewModel : DialogViewModel
{
    public static readonly string[] Icons = ["Calendar", "Home", "Suitcase", "Games", "Travel", "Projects", "Warning", "Star", "Flag", "Clock", "Notes", "Diagnostic", "Trophy", "Sparkle", "Pin", "Bell"];
    public static readonly string[] Accents = ["Blue", "Cyan", "Green", "Amber", "Purple", "Red", "Gold", "Steel"];
    private readonly ILocalizer _loc;

    public CategoryEditorViewModel(ILocalizer loc, CalendarCategory? existing)
    {
        _loc = loc;
        IsNew = existing == null;
        Title = loc[IsNew ? "Cat_NewTitle" : "Cat_EditTitle"];
        _name = existing == null ? string.Empty : EventEditorViewModel.CategoryName(existing, loc);
        _glyph = existing?.Icon ?? "Calendar";
        _accent = existing?.Accent ?? "Blue";
    }

    public override string Icon => "Calendar";
    public override double DialogWidth => 440;
    public bool IsNew { get; }
    public IReadOnlyList<string> IconChoices => Icons;
    public IReadOnlyList<string> AccentChoices => Accents;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _glyph;
    [ObservableProperty] private string _accent;
    [ObservableProperty] private string? _error;

    [RelayCommand] private void ChooseIcon(string? icon) { if (icon != null) Glyph = icon; }
    [RelayCommand] private void ChooseAccent(string? accent) { if (accent != null) Accent = accent; }

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = _loc["Cat_ErrName"]; return; }
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}
