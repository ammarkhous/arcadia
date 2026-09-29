using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Arcadia.App.Services;
using Arcadia.Core.Automation;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using Arcadia.Core.Settings;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arcadia.App.ViewModels.Automation;

public enum AutomationTab { Automations, Templates, Activity, Triggers, Sessions, Engine }

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One setting of a step, edited with a control chosen by its kind.</summary>
public sealed partial class FieldViewModel : ObservableObject
{
    public FieldViewModel(FieldSpec spec, string? value, IReadOnlyList<Choice> choices, string label)
    {
        Spec = spec;
        Label = label + (spec.Required ? "" : " ·");
        Choices = choices;
        _value = value ?? spec.Default ?? string.Empty;
        if (spec.Kind == FieldKind.Days)
        {
            var mask = AutomationText.ParseDays(_value);
            Days = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames
                .Select((_, i) => new DayToggle(i, mask.HasFlag((Arcadia.Core.Organizer.WeekdayMask)(1 << i)), this)).ToList();
        }
    }

    public FieldSpec Spec { get; }
    public string Key => Spec.Key;
    public string Kind => Spec.Kind.ToString();
    public string Label { get; }
    public IReadOnlyList<Choice> Choices { get; }
    public IReadOnlyList<DayToggle> Days { get; } = [];
    public bool IsLtr => Spec.Kind is FieldKind.Executable or FieldKind.Folder or FieldKind.Uri or FieldKind.Time or FieldKind.Date or FieldKind.Extensions;

    [ObservableProperty] private string _value;
    [ObservableProperty] private IReadOnlyList<string> _runningProcesses = [];
    [ObservableProperty] private bool _isShown = true;

    /// <summary>Raised when the value changes (conditional fields re-evaluate).</summary>
    internal event Action? Changed;

    public Choice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value);
        set { if (value != null) Value = value.Value; OnPropertyChanged(); }
    }

    public bool Flag
    {
        get => Value is "true" or "1";
        set { Value = value ? "true" : "false"; OnPropertyChanged(); }
    }

    internal void DaysChanged() => Value = AutomationText.FormatDays(Days.Where(d => d.IsOn).Aggregate(Arcadia.Core.Organizer.WeekdayMask.None, (m, d) => m | (Arcadia.Core.Organizer.WeekdayMask)(1 << d.Index)));

    [RelayCommand]
    private void Browse()
    {
        if (Spec.Kind == FieldKind.Folder)
        {
            var dlg = new OpenFolderDialog { Multiselect = false };
            if (dlg.ShowDialog() == true) Value = dlg.FolderName;
        }
        else if (Spec.Kind == FieldKind.Executable)
        {
            var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
            if (dlg.ShowDialog() == true) Value = Path.GetFileName(dlg.FileName);
        }
    }

    /// <summary>Running executables (names only) to pick from: "Detect running process".</summary>
    [RelayCommand]
    private void Detect() => RunningProcesses = AutomationService.ProcessNames().Where(n => n.EndsWith(".exe")).Order().ToList();

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedChoice));
        Changed?.Invoke();
    }
}

public sealed partial class DayToggle(int index, bool on, FieldViewModel owner) : ObservableObject
{
    public int Index { get; } = index;
    public string Label => CultureInfo.CurrentUICulture.DateTimeFormat.AbbreviatedDayNames[Index];
    [ObservableProperty] private bool _isOn = on;
    partial void OnIsOnChanged(bool value) => owner.DaysChanged();
}

/// <summary>A trigger, condition or action being edited: a registered type plus its fields.</summary>
public sealed partial class StepViewModel : ObservableObject
{
    private readonly AutomationViewModel _owner;

    public StepViewModel(AutomationViewModel owner, string role, IReadOnlyList<Choice> types, AutomationStep? step)
    {
        _owner = owner;
        Role = role;
        Types = types;
        _type = types.FirstOrDefault(t => t.Value == step?.Type) ?? types.First();
        Rebuild(step?.Settings ?? []);
    }

    public string Role { get; }
    public IReadOnlyList<Choice> Types { get; }
    public ObservableCollection<FieldViewModel> Fields { get; } = [];
    [ObservableProperty] private Choice _type;
    [ObservableProperty] private int _number;
    [ObservableProperty] private bool _needsConfirmation;
    [ObservableProperty] private string _icon = "Sparkle";

    partial void OnTypeChanged(Choice value) => Rebuild(Fields.ToDictionary(f => f.Key, f => f.Value));

    private void Rebuild(Dictionary<string, string> values)
    {
        Fields.Clear();
        var descriptor = _owner.Describe(Role, Type.Value);
        if (descriptor == null) return;
        Icon = descriptor.Icon;
        NeedsConfirmation = descriptor is ActionDescriptor { Safety: ActionSafety.Confirm };
        foreach (var f in descriptor.Fields)
        {
            var field = new FieldViewModel(f, values.GetValueOrDefault(f.Key), _owner.ChoicesFor(f), _owner.FieldLabel(f.Key));
            field.Changed += UpdateShown;
            Fields.Add(field);
        }
        UpdateShown();
    }

    /// <summary>Shows only the fields that apply (a schedule's days only when weekly…).</summary>
    private void UpdateShown()
    {
        var step = new AutomationStep(Type.Value, Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key, f => f.Value));
        foreach (var f in Fields) f.IsShown = f.Spec.AppliesTo(step);
    }

    public AutomationStep ToStep() =>
        new(Type.Value, Fields.Where(f => f.IsShown && !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key, f => f.Value.Trim()));

    [RelayCommand] private void Remove() => _owner.RemoveStep(this);
    [RelayCommand] private void MoveUp() => _owner.MoveStep(this, -1);
    [RelayCommand] private void MoveDown() => _owner.MoveStep(this, +1);
}

/// <summary>A row in the automation list.</summary>
public sealed record AutomationRow(AutomationDefinition Definition, string Title, string Summary, string State, string StateGlyph, string LastRun, string LastResult, string Tone);

public sealed record RunRow(AutomationRunRecord Run, string Time, string Name, string Trigger, string Status, string Glyph, string Duration, string Summary, string Tone);

public sealed record DetailLine(string Label, string Value, bool Ok = true);

public sealed record TemplateRow(string Key, string Title, string Description, string Icon, AutomationDefinition Definition);

public sealed record CatalogRow(string Group, string Title, string Description, string Icon, string Badge);

public sealed record WorkRow(string Project, string When, string Duration, string Note);

/// <summary>AUTOMATION — Arcadia's control room (Phase 9).</summary>
public sealed partial class AutomationViewModel : ObservableObject, IActivatable, IShortcutTarget
{
    private readonly AutomationService _service;
    private readonly PerformanceService _performance;
    private readonly SessionTrackingService _tracking;
    private readonly ISettingsService _settings;
    private readonly ILocalizer _loc;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IGameCatalog _games;
    private readonly IProjectStore _projects;
    private readonly ITravelStore _travel;
    private readonly IWorkSessionStore _work;
    private readonly ILogger<AutomationViewModel> _logger;
    private List<Choice> _gameChoices = [], _projectChoices = [], _tripChoices = [];

    public AutomationViewModel(AutomationService service, PerformanceService performance, SessionTrackingService tracking, ISettingsService settings, ILocalizer loc,
        IDialogService dialogs, INotificationService notifications, IGameCatalog games, IProjectStore projects, ITravelStore travel, IWorkSessionStore work,
        StartupState startup, ILogger<AutomationViewModel> logger)
    {
        _startup = startup;
        _service = service;
        _performance = performance;
        _tracking = tracking;
        _settings = settings;
        _loc = loc;
        _dialogs = dialogs;
        _notifications = notifications;
        _games = games;
        _projects = projects;
        _travel = travel;
        _work = work;
        _logger = logger;
        service.RunRecorded += run => { if (Tab == AutomationTab.Activity) _ = LoadRunsAsync(); _ = RefreshListAsync(reload: false); };
        service.Changed += (_, _) => { RefreshState(); _ = RefreshListAsync(reload: false); };
        performance.Changed += (_, _) => RefreshState();
    }

    public PerformanceService Performance => _performance;
    public NowPlayingState NowPlaying => _tracking.NowPlaying;
    public AutomationService Service => _service;

    [ObservableProperty] private AutomationTab _tab = AutomationTab.Automations;
    public ObservableCollection<AutomationRow> Automations { get; } = [];
    [ObservableProperty] private AutomationRow? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _engineState = string.Empty;
    [ObservableProperty] private string _engineGlyph = "●";
    [ObservableProperty] private string _engineTone = "Green";
    [ObservableProperty] private string _counts = string.Empty;

    // editor
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _cooldownMinutes = "0";
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private bool _anyCondition;
    [ObservableProperty] private StepViewModel? _trigger;
    public ObservableCollection<StepViewModel> Conditions { get; } = [];
    public ObservableCollection<StepViewModel> Actions { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testOk;

    // activity
    public ObservableCollection<RunRow> Runs { get; } = [];
    [ObservableProperty] private RunRow? _selectedRun;
    public ObservableCollection<DetailLine> RunDetails { get; } = [];
    [ObservableProperty] private string _runFilter = "all";
    [ObservableProperty] private string? _runTechnical;

    // catalog, templates, sessions
    public List<CatalogRow> Catalog { get; private set; } = [];
    public List<TemplateRow> Templates { get; private set; } = [];
    [ObservableProperty] private string _sourcesText = string.Empty;
    public ObservableCollection<WorkRow> WorkSessions { get; } = [];
    public List<Choice> ProjectChoices => _projectChoices.Where(c => c.Value != "any").ToList();
    [ObservableProperty] private Choice? _workProject;
    [ObservableProperty] private string _workNote = string.Empty;
    [ObservableProperty] private string _workElapsed = string.Empty;

    // engine settings
    public AutomationSettings Prefs => _settings.Current.Automation;
    public TrackingSettings TrackingPrefs => _settings.Current.Tracking;
    public PerformanceSettings PerformancePrefs => _settings.Current.Performance;

    private readonly StartupState _startup;

    public async Task ActivateAsync()
    {
        await _startup.DatabaseReady;
        await _service.EnsureLoadedAsync();
        await LoadPickersAsync();
        BuildStatic();
        RefreshState();
        await RefreshListAsync(reload: false);
        if (Tab == AutomationTab.Activity) await LoadRunsAsync();
        if (Tab == AutomationTab.Sessions) await LoadSessionsAsync();
    }

    public bool OnNew() { NewAutomation(); return true; }
    public bool OnSave() { _ = SaveAsync(); return true; }

    partial void OnTabChanged(AutomationTab value)
    {
        if (value == AutomationTab.Activity) _ = LoadRunsAsync();
        if (value == AutomationTab.Sessions) _ = LoadSessionsAsync();
        if (value == AutomationTab.Triggers) SourcesText = BuildSourcesText();
    }

    [RelayCommand] private void ShowTab(AutomationTab tab) => Tab = tab;

    private void RefreshState()
    {
        if (!_service.Engine.IsEnabled) { EngineState = _loc["Auto_StateDisabled"]; EngineGlyph = "○"; EngineTone = "Steel"; }
        else if (_service.IsPaused) { EngineState = _loc["Auto_StatePaused"]; EngineGlyph = "Ⅱ"; EngineTone = "Amber"; }
        else { EngineState = _loc["Auto_StateActive"]; EngineGlyph = "●"; EngineTone = "Green"; }
        var all = _service.Automations;
        Counts = _loc.Format("Auto_Counts", all.Count(a => a.Enabled), all.Count, all.Count(a => a.LastStatus == RunStatus.Failed));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsGamingMode));
        OnPropertyChanged(nameof(IsPowerSaver));
        PowerText = _loc.Format("Auto_PowerLine", Math.Max(_performance.MemoryLoad, PerformanceService.ReadMemoryLoad()), _loc[_performance.IsOnBattery ? "Auto_Choice_battery" : "Auto_Choice_ac"]);
    }

    [ObservableProperty] private string _powerText = string.Empty;

    public bool IsPaused { get => _service.IsPaused; set => _ = _service.SetPausedAsync(value); }
    public bool IsGamingMode { get => _performance.IsGamingMode; set => _performance.SetGamingMode(value, "manual"); }
    public bool IsPowerSaver { get => _performance.IsPowerSaver; set { if (value != _performance.IsPowerSaver) _performance.TogglePowerSaver(); } }

    public bool EngineEnabled
    {
        get => Prefs.Enabled;
        set { Prefs.Enabled = value; _service.SetEngineEnabled(value); _ = _settings.SaveAsync(); RefreshState(); OnPropertyChanged(); }
    }

    // ── list ──

    private async Task RefreshListAsync(bool reload)
    {
        if (reload) await _service.ReloadAsync();
        var keep = Selected?.Definition.Id;
        Automations.Clear();
        foreach (var d in _service.Automations)
        {
            var (state, glyph, tone) = !d.Enabled ? (_loc["Auto_RowDisabled"], "○", "Steel")
                : d.LastStatus == RunStatus.Failed ? (_loc["Auto_RowError"], "⚠", "Red")
                : _service.IsPaused ? (_loc["Auto_RowPaused"], "Ⅱ", "Amber")
                : (_loc["Auto_RowActive"], "●", "Green");
            Automations.Add(new AutomationRow(d, d.Name, Summarize(d), state, glyph,
                d.LastRunAtUtc is { } t ? t.ToLocalTime().ToString("g", _loc.Culture) : "—",
                d.LastStatus is { } s ? _loc["Auto_Status_" + s] : "", tone));
        }
        if (keep != null) Selected = Automations.FirstOrDefault(r => r.Definition.Id == keep);
        RefreshState();
    }

    public string Summarize(AutomationDefinition d)
    {
        var first = d.Trigger.Settings.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var trigger = _loc["Auto_Trigger_" + d.Trigger.Type] + (first != null ? " · " + DisplayValue(d.Trigger, first) : "");
        var actions = string.Join(", ", d.Actions.Select(a => _loc["Auto_Action_" + a.Type]));
        return trigger + "  →  " + actions;
    }

    private string DisplayValue(AutomationStep step, string value)
    {
        foreach (var key in new[] { "game", "project", "trip" })
            if (step.Get(key) == value)
            {
                var list = key == "game" ? _gameChoices : key == "project" ? _projectChoices : _tripChoices;
                return list.FirstOrDefault(c => c.Value == value)?.Label ?? value;
            }
        return value;
    }

    partial void OnSelectedChanged(AutomationRow? value)
    {
        if (value != null) Edit(value.Definition);
    }

    // ── editor ──

    internal StepDescriptor? Describe(string role, string type) => role switch
    {
        "trigger" => _service.Registry.Trigger(type),
        "condition" => _service.Registry.Condition(type),
        _ => _service.Registry.Action(type),
    };

    private IReadOnlyList<Choice> TypesFor(string role) => role switch
    {
        "trigger" => _service.Registry.Triggers.Select(t => new Choice(t.Id, _loc["Auto_Trigger_" + t.Id])).ToList(),
        "condition" => _service.Registry.Conditions.Select(t => new Choice(t.Id, _loc["Auto_Condition_" + t.Id])).ToList(),
        _ => _service.Registry.Actions.Select(t => new Choice(t.Id, _loc["Auto_Action_" + t.Id] + (t.Safety == ActionSafety.Confirm ? "  ⚑" : ""))).ToList(),
    };

    internal string FieldLabel(string key) => _loc["Auto_Field_" + key];

    internal IReadOnlyList<Choice> ChoicesFor(FieldSpec f) => f.Kind switch
    {
        FieldKind.Game => _gameChoices.Where(c => !f.Required || c.Value != "any").ToList(),
        FieldKind.Project => _projectChoices.Where(c => !f.Required || c.Value != "any").ToList(),
        FieldKind.Trip => _tripChoices.Where(c => !f.Required || c.Value != "any").ToList(),
        FieldKind.Section => AutomationCatalog.Sections.Select(s => new Choice(s, _loc["Orc_Section_" + s])).ToList(),
        FieldKind.Choice when f.Choices != null => (f.Required ? [] : new[] { new Choice("", "—") }).Concat(f.Choices.Select(c => new Choice(c, _loc["Auto_Choice_" + c]))).ToList(),
        _ => [],
    };

    private async Task LoadPickersAsync()
    {
        var any = new Choice("any", _loc["Auto_Any"]);
        try
        {
            _gameChoices = [any, .. (await _games.GetGamesAsync()).OrderBy(g => g.Title).Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Title))];
            _projectChoices = [any, .. (await _projects.ListAsync()).OrderBy(p => p.Name).Select(p => new Choice(p.Id.ToString(CultureInfo.InvariantCulture), p.Name))];
            _tripChoices = [any, .. (await _travel.ListTripsAsync()).OrderBy(t => t.Name).Select(t => new Choice(t.Id.ToString(CultureInfo.InvariantCulture), t.Name))];
            OnPropertyChanged(nameof(ProjectChoices));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Loading automation pickers failed"); }
    }

    [RelayCommand]
    public void NewAutomation()
    {
        Selected = null;
        Edit(new AutomationDefinition(0, _loc["Auto_NewName"], null, true, new AutomationStep("app.started"), ConditionMode.All, [], [new AutomationStep("notify", new() { ["title"] = "Arcadia" })]));
        Tab = AutomationTab.Automations;
    }

    public void Edit(AutomationDefinition d)
    {
        EditId = d.Id;
        Name = d.Name;
        Description = d.Description ?? string.Empty;
        Enabled = d.Enabled;
        CooldownMinutes = (d.CooldownSeconds / 60).ToString(CultureInfo.InvariantCulture);
        Tags = d.Tags;
        AnyCondition = d.Mode == ConditionMode.Any;
        Trigger = new StepViewModel(this, "trigger", TypesFor("trigger"), d.Trigger);
        Conditions.Clear();
        foreach (var c in d.Conditions) Conditions.Add(new StepViewModel(this, "condition", TypesFor("condition"), c));
        Actions.Clear();
        foreach (var a in d.Actions) Actions.Add(new StepViewModel(this, "action", TypesFor("action"), a));
        Renumber();
        Errors.Clear();
        TestResult = null;
        IsEditing = true;
    }

    private AutomationDefinition Build() => new(EditId, Name, Description, Enabled, Trigger!.ToStep(), AnyCondition ? ConditionMode.Any : ConditionMode.All,
        Conditions.Select(c => c.ToStep()).ToList(), Actions.Select(a => a.ToStep()).ToList(),
        (int.TryParse(CooldownMinutes, out var m) ? Math.Clamp(m, 0, 10080) : 0) * 60, Tags);

    private bool Validate(AutomationDefinition d)
    {
        Errors.Clear();
        foreach (var e in _service.Registry.Validate(d))
        {
            var parts = e.Split(':', 2);
            Errors.Add(_loc[parts[0]] + (parts.Length > 1 ? " — " + StepLabel(parts[1]) : ""));
        }
        return Errors.Count == 0;
    }

    private string StepLabel(string type) =>
        _service.Registry.Trigger(type) != null ? _loc["Auto_Trigger_" + type]
        : _service.Registry.Condition(type) != null ? _loc["Auto_Condition_" + type]
        : _service.Registry.Action(type) != null ? _loc["Auto_Action_" + type] : type;

    [RelayCommand] private void AddCondition() { Conditions.Add(new StepViewModel(this, "condition", TypesFor("condition"), null)); }
    [RelayCommand] private void AddAction() { Actions.Add(new StepViewModel(this, "action", TypesFor("action"), null)); Renumber(); }

    internal void RemoveStep(StepViewModel s)
    {
        if (!Conditions.Remove(s)) Actions.Remove(s);
        Renumber();
    }

    internal void MoveStep(StepViewModel s, int delta)
    {
        var i = Actions.IndexOf(s);
        if (i < 0 || i + delta < 0 || i + delta >= Actions.Count) return;
        Actions.Move(i, i + delta);
        Renumber();
    }

    private void Renumber() { for (var i = 0; i < Actions.Count; i++) Actions[i].Number = i + 1; }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Trigger == null) return;
        var d = Build();
        if (!Validate(d)) return;
        try
        {
            var saved = await _service.SaveAsync(d);
            EditId = saved.Id;
            await RefreshListAsync(reload: false);
            Selected = Automations.FirstOrDefault(r => r.Definition.Id == saved.Id);
            _notifications.Show(_loc.Format("Auto_Saved", saved.Name), NotificationKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving automation failed");
            Errors.Add(ex.Message);
        }
    }

    /// <summary>TEST: runs against a clearly labelled simulated event (actions really run).</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (Trigger == null) return;
        var d = Build();
        if (!Validate(d)) return;
        if (d.Actions.Any(a => _service.Registry.Action(a.Type)?.Safety == ActionSafety.Confirm)
            && !_dialogs.Confirm(_loc["Auto_TestConfirm"], _loc["Auto_Test"])) return;
        try
        {
            var run = await _service.Engine.TestAsync(d);
            TestOk = run.Status == RunStatus.Success;
            TestResult = _loc.Format("Auto_TestResult", _loc["Auto_Status_" + run.Status], run.Summary,
                string.Join(" · ", run.Details.Conditions.Select(c => (c.Ok ? "✓ " : "✗ ") + _loc["Auto_Condition_" + c.Type])));
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestResult = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditId == 0) { IsEditing = false; return; }
        if (!_dialogs.Confirm(_loc.Format("Auto_DeleteConfirm", Name), _loc["Auto_Delete"])) return;
        await _service.DeleteAsync(EditId);
        IsEditing = false;
        Selected = null;
        await RefreshListAsync(reload: false);
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(AutomationRow? row)
    {
        if (row == null) return;
        await _service.SetEnabledAsync(row.Definition.Id, !row.Definition.Enabled);
        await RefreshListAsync(reload: false);
    }

    [RelayCommand]
    private void Duplicate()
    {
        var d = Build() with { Id = 0, Name = Name + " " + _loc["Auto_CopySuffix"], Enabled = false };
        Selected = null;
        Edit(d);
    }

    // ── templates ──

    [RelayCommand]
    private void UseTemplate(TemplateRow? t)
    {
        if (t == null) return;
        Selected = null;
        Edit(t.Definition);
        Tab = AutomationTab.Automations;
    }

    private void BuildStatic()
    {
        var firstGame = _gameChoices.Skip(1).FirstOrDefault()?.Value ?? "any";
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        AutomationStep S(string type, params (string K, string V)[] kv) => new(type, kv.ToDictionary(x => x.K, x => x.V));
        Templates =
        [
            new("game", _loc["Auto_Tpl_Game"], _loc["Auto_TplDesc_Game"], "Games", new(0, _loc["Auto_Tpl_Game"], _loc["Auto_TplDesc_Game"], true,
                S("game.started", ("game", firstGame)), ConditionMode.All, [], [S("game.playing"), S("gaming.on"), S("notify", ("title", "{gameTitle}"), ("text", _loc["Auto_TplText_Game"]))])),
            new("gamestop", _loc["Auto_Tpl_GameStop"], _loc["Auto_TplDesc_GameStop"], "Games", new(0, _loc["Auto_Tpl_GameStop"], _loc["Auto_TplDesc_GameStop"], true,
                S("game.stopped", ("game", firstGame)), ConditionMode.All, [], [S("quickcapture", ("kind", "Note"), ("text", "{gameTitle}: "))])),
            new("morning", _loc["Auto_Tpl_Morning"], _loc["Auto_TplDesc_Morning"], "Agenda", new(0, _loc["Auto_Tpl_Morning"], _loc["Auto_TplDesc_Morning"], true,
                S("app.started"), ConditionMode.All, [S("time.between", ("after", "06:00"), ("before", "11:00"))], [S("open.today")])),
            new("downloads", _loc["Auto_Tpl_Downloads"], _loc["Auto_TplDesc_Downloads"], "Import", new(0, _loc["Auto_Tpl_Downloads"], _loc["Auto_TplDesc_Downloads"], true,
                S("file.created", ("folder", downloads)), ConditionMode.All, [S("file.extension", ("extensions", "pdf"))], [S("notify", ("title", _loc["Auto_TplText_Downloads"]), ("text", "{file}"))])),
            new("project", _loc["Auto_Tpl_Project"], _loc["Auto_TplDesc_Project"], "Projects", new(0, _loc["Auto_Tpl_Project"], _loc["Auto_TplDesc_Project"], true,
                S("project.status", ("to", "Completed")), ConditionMode.All, [], [S("notify", ("title", "{projectName}"), ("text", _loc["Auto_TplText_Project"])), S("note.create", ("title", _loc["Auto_TplNote_Project"]), ("text", "{projectName} · {date}"))])),
            new("bill", _loc["Auto_Tpl_Bill"], _loc["Auto_TplDesc_Bill"], "Receipt", new(0, _loc["Auto_Tpl_Bill"], _loc["Auto_TplDesc_Bill"], true,
                S("bill.due", ("days", "2")), ConditionMode.All, [], [S("notify", ("title", "{billName}"), ("text", _loc["Auto_TplText_Bill"]))])),
        ];
        Catalog =
        [
            .. _service.Registry.Triggers.Select(t => new CatalogRow(_loc["Auto_When"], _loc["Auto_Trigger_" + t.Id], _loc["Auto_Cat_" + t.Category], t.Icon, "")),
            .. _service.Registry.Conditions.Select(c => new CatalogRow(_loc["Auto_If"], _loc["Auto_Condition_" + c.Id], _loc["Auto_Cat_" + c.Category], c.Icon, "")),
            .. _service.Registry.Actions.Select(a => new CatalogRow(_loc["Auto_Then"], _loc["Auto_Action_" + a.Id], _loc["Auto_Cat_" + a.Category], a.Icon,
                a.Safety == ActionSafety.Confirm ? _loc["Auto_SafetyConfirm"] : _loc["Auto_SafetySafe"])),
        ];
        OnPropertyChanged(nameof(Templates));
        OnPropertyChanged(nameof(Catalog));
    }

    private string BuildSourcesText()
    {
        var types = _service.Engine.ActiveTriggerTypes();
        var lines = new List<string>
        {
            _loc.Format("Auto_SrcActive", types.Count == 0 ? "—" : string.Join(", ", types.Select(t => _loc["Auto_Trigger_" + t]))),
            _loc.Format("Auto_SrcDetail", _service.SourceSummary),
        };
        if (_service.NextClockCheck is { } next) lines.Add(_loc.Format("Auto_SrcNext", next.ToString("g", _loc.Culture)));
        lines.Add(_loc.Format("Auto_SrcSuppressed", _service.Engine.SuppressedDuplicates, _service.Engine.BlockedLoops));
        return string.Join("\n", lines);
    }

    // ── activity ──

    partial void OnRunFilterChanged(string value) => _ = LoadRunsAsync();

    [RelayCommand] private void FilterRuns(string filter) => RunFilter = filter;

    private async Task LoadRunsAsync()
    {
        var runs = await _service.RecentRunsAsync(300);
        Runs.Clear();
        foreach (var r in runs.Where(r => RunFilter switch
                 {
                     "failed" => r.Status is RunStatus.Failed or RunStatus.Incomplete,
                     "blocked" => r.Status == RunStatus.Blocked,
                     "skipped" => r.Status == RunStatus.Skipped,
                     _ => true,
                 }))
            Runs.Add(new RunRow(r, r.StartedAtUtc.ToLocalTime().ToString("dd MMM HH:mm:ss", _loc.Culture), r.AutomationName, _loc["Auto_Trigger_" + r.TriggerType],
                _loc["Auto_Status_" + r.Status] + (r.IsSimulation ? " · " + _loc["Auto_Simulated"] : ""), Glyph(r.Status),
                r.DurationMs < 1000 ? r.DurationMs + " ms" : (r.DurationMs / 1000.0).ToString("0.0", _loc.Culture) + " s", r.Summary, Tone(r.Status)));
    }

    public static string Glyph(RunStatus s) => s switch { RunStatus.Success => "✓", RunStatus.Skipped => "–", RunStatus.Blocked => "⛔", RunStatus.Running => "…", _ => "⚠" };
    public static string Tone(RunStatus s) => s switch { RunStatus.Success => "Green", RunStatus.Skipped => "Steel", RunStatus.Blocked => "Amber", RunStatus.Running => "Blue", _ => "Red" };

    partial void OnSelectedRunChanged(RunRow? value)
    {
        RunDetails.Clear();
        RunTechnical = null;
        if (value == null) return;
        var d = value.Run.Details;
        if (value.Run.IsSimulation) RunDetails.Add(new(_loc["Auto_Simulated"], _loc["Auto_SimulatedNote"]));
        foreach (var (k, v) in d.Trigger.Where(kv => kv.Key is not ("time" or "date"))) RunDetails.Add(new(_loc["Auto_Data"] + " · " + k, v));
        foreach (var c in d.Conditions) RunDetails.Add(new(_loc["Auto_If"] + " · " + _loc["Auto_Condition_" + c.Type], c.Ok ? "✓" : "✗", c.Ok));
        foreach (var a in d.Actions) RunDetails.Add(new(_loc["Auto_Then"] + " · " + _loc["Auto_Action_" + a.Type], (a.Ok ? "✓ " : "✗ ") + (a.Note ?? ""), a.Ok));
        if (d.Error != null) RunDetails.Add(new(_loc["Auto_Error"], d.Error, false));
        RunDetails.Add(new(_loc["Auto_Duration"], value.Duration));
        if (_settings.Current.Oracle.DeveloperDiagnostics) RunTechnical = d.Technical ?? "—";
    }

    [RelayCommand]
    private async Task ClearActivityAsync()
    {
        if (!_dialogs.Confirm(_loc["Auto_ClearLogConfirm"], _loc["Auto_ClearLog"])) return;
        await _service.ClearLogAsync();
        await LoadRunsAsync();
    }

    // ── import / export ──

    [RelayCommand]
    private async Task ExportAsync()
    {
        var list = Selected != null ? [Selected.Definition] : _service.Automations.ToList();
        if (list.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "Arcadia automation (*.arcadia-automation)|*.arcadia-automation", FileName = (Selected?.Title ?? "automations") + AutomationTransfer.Extension };
        if (dlg.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dlg.FileName, AutomationTransfer.Export(list, DateTime.UtcNow));
        _notifications.Show(_loc.Format("Auto_Exported", list.Count), NotificationKind.Success);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Arcadia automation (*.arcadia-automation;*.json)|*.arcadia-automation;*.json" };
        if (dlg.ShowDialog() != true) return;
        var result = await ImportFileAsync(dlg.FileName);
        _notifications.Show(result, NotificationKind.Info);
    }

    /// <summary>Imports a file: validated, saved <b>disabled</b>, never run. Returns the summary shown to the user.</summary>
    public async Task<string> ImportFileAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 512 * 1024) return _loc["Auto_ImportTooLarge"];
        var result = AutomationTransfer.Import(await File.ReadAllTextAsync(path), _service.Registry);
        foreach (var d in result.Accepted) await _service.SaveAsync(d);
        await _service.ReloadAsync();
        await RefreshListAsync(reload: false);
        var errors = result.Errors.Select(e => e.Contains(':') && !e.StartsWith('#') ? _loc[e] : e).ToList();
        return _loc.Format("Auto_Imported", result.Accepted.Count, errors.Count) + (errors.Count > 0 ? "\n" + string.Join("\n", errors.Take(5)) : "");
    }

    // ── session center ──

    private async Task LoadSessionsAsync()
    {
        await LoadPickersAsync();
        WorkSessions.Clear();
        foreach (var (s, project) in await _work.RecentAsync(15))
            WorkSessions.Add(new WorkRow(project, s.StartedAtUtc.ToLocalTime().ToString("g", _loc.Culture),
                s.EndedAtUtc == null ? _loc["Auto_Running"] : Arcadia.Core.Formatting.RelativeTime.Hours(s.Minutes / 60.0, _loc), s.Note ?? ""));
        WorkProject ??= ProjectChoices.FirstOrDefault();
        UpdateWorkElapsed();
    }

    private void UpdateWorkElapsed() =>
        WorkElapsed = _service.ActiveWork is { } w ? _loc.Format("Auto_WorkingOn", _service.ActiveWorkProject ?? "", Math.Max(0, (int)(DateTime.UtcNow - w.StartedAtUtc).TotalMinutes)) : _loc["Auto_NoWork"];

    [RelayCommand]
    private async Task StartWorkAsync()
    {
        if (WorkProject == null || !int.TryParse(WorkProject.Value, out var id)) return;
        await _service.StartWorkSessionAsync(id, automatic: false, CancellationToken.None);
        await LoadSessionsAsync();
    }

    [RelayCommand]
    private async Task StopWorkAsync()
    {
        await _service.StopWorkAsync(string.IsNullOrWhiteSpace(WorkNote) ? null : WorkNote);
        WorkNote = string.Empty;
        await LoadSessionsAsync();
    }

    [RelayCommand] private Task StopGameAsync() => _tracking.StopCurrentAsync();

    [RelayCommand]
    private void SaveEngineSettings()
    {
        _ = _settings.SaveAsync();
        _settings.NotifyChanged();
    }
}
