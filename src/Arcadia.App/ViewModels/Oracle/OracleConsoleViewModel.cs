using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using Arcadia.App.Converters;
using Arcadia.App.Messages;
using Arcadia.App.Services;
using Arcadia.Core.Finance;
using Arcadia.Core.Localization;
using Arcadia.Core.Oracle;
using Arcadia.Core.Organizer;
using Arcadia.UI.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Oracle;

public sealed class OracleActionViewModel(OracleAction action, OracleEntryViewModel owner)
{
    public OracleAction Action { get; } = action;
    public OracleEntryViewModel Owner { get; } = owner;
    public string Label => Action.Label;
    public string Icon => Action.Icon;
    public bool IsPrimary => Action.IsPrimary;
    public bool IsEnabled => Action.IsEnabled;
}

public sealed partial class OracleItemViewModel : ObservableObject
{
    private readonly OracleItem _item;

    public OracleItemViewModel(OracleItem item, OracleEntryViewModel owner, string? groupHeader)
    {
        _item = item;
        GroupHeader = groupHeader;
        Actions = item.Actions.Select(a => new OracleActionViewModel(a, owner)).ToList();
        AccentBrush = Tones.Frozen(new SolidColorBrush(ToneToColorConverter.Resolve(item.Accent)));
    }

    public string Title => _item.Title;
    public string? Subtitle => _item.Subtitle;
    public string? Value => _item.ValueIsMoney ? FinancePrivacy.Apply(_item.Value, FinancePrivacy.Instance.IsHidden) : _item.Value;
    public string Icon => _item.Icon;
    public Brush AccentBrush { get; }
    public double Progress => _item.Progress ?? 0;
    public bool HasProgress => _item.Progress != null;
    public bool IsMuted => _item.IsMuted;
    public string? GroupHeader { get; }
    public List<OracleActionViewModel> Actions { get; }
    public OracleActionViewModel? Primary => Actions.FirstOrDefault(a => a.IsPrimary && a.IsEnabled) ?? Actions.FirstOrDefault(a => a.IsEnabled);
    public void RefreshPrivacy() => OnPropertyChanged(nameof(Value));
}

public sealed partial class OracleMetricViewModel(OracleMetric metric) : ObservableObject
{
    public string Label => metric.Label;
    public string Value => metric.IsMoney ? FinancePrivacy.Apply(metric.Value, FinancePrivacy.Instance.IsHidden) : metric.Value;
    public Brush ToneBrush { get; } = metric.Tone == null
        ? (Brush)System.Windows.Application.Current.FindResource("Brush.Text.Primary")
        : Tones.Frozen(new SolidColorBrush(ToneToColorConverter.Resolve(metric.Tone)));
    public void RefreshPrivacy() => OnPropertyChanged(nameof(Value));
}

public sealed record OracleFieldViewModel(string Label, string Value);

/// <summary>One command and its structured result, as shown in the transcript and the overlay.</summary>
public sealed partial class OracleEntryViewModel : ObservableObject
{
    public OracleEntryViewModel(string input, OracleResult result, ILocalizer loc, bool showTrace)
    {
        Input = input;
        Result = result;
        At = DateTime.Now;
        string? lastGroup = null;
        foreach (var item in result.Items)
        {
            var header = item.Group != null && item.Group != lastGroup ? item.Group : null;
            lastGroup = item.Group;
            Items.Add(new OracleItemViewModel(item, this, header));
        }
        Metrics = result.Metrics.Select(m => new OracleMetricViewModel(m)).ToList();
        Actions = result.Actions.Select(a => new OracleActionViewModel(a, this)).ToList();
        if (result.Preview is { } p)
        {
            PreviewFields = p.Fields.Select(f => new OracleFieldViewModel(f.Label, f.Label == loc["Orc_Field_Amount"] ? FinancePrivacy.Apply(f.Value, FinancePrivacy.Instance.IsHidden) : f.Value)).ToList();
            Confirm = new OracleActionViewModel(p.Confirm, this);
            if (p.Edit != null) Edit = new OracleActionViewModel(p.Edit, this);
            Cancel = new OracleActionViewModel(new OracleAction { Label = loc["Orc_Cancel"], Icon = "Close", Kind = OracleActionKind.Cancel }, this);
        }
        Badge = loc["Orc_Badge_" + result.Kind] + (result.Kind == OracleResultKind.List && result.Items.Count > 0 ? " · " + result.Items.Count : string.Empty);
        ConfidenceLabel = loc["Orc_Conf_" + result.Confidence];
        Trace = showTrace && result.Trace is { } t ? OracleHost.DescribeTrace(t) : null;
    }

    public string Input { get; }
    public OracleResult Result { get; }
    public DateTime At { get; }
    public string Time => At.ToString("HH:mm:ss");
    public string Headline => Result.HeadlineIsMoney ? FinancePrivacy.Apply(Result.Headline, FinancePrivacy.Instance.IsHidden) : Result.Headline;
    public string? Detail => Result.Detail;
    public string Kind => Result.Kind.ToString();
    public string Badge { get; }
    public string ConfidenceLabel { get; }
    public string Confidence => Result.Confidence.ToString();
    public ObservableCollection<OracleItemViewModel> Items { get; } = [];
    public List<OracleMetricViewModel> Metrics { get; }
    public List<OracleActionViewModel> Actions { get; }
    public List<string> Suggestions => Result.Suggestions;
    public bool HasPreview => Result.Preview != null;
    public string? PreviewTitle => Result.Preview?.Title;
    public string PreviewIcon => Result.Preview?.Icon ?? "Plus";
    public List<OracleFieldViewModel>? PreviewFields { get; }
    public List<string>? PreviewNotes => Result.Preview?.Notes;
    public OracleActionViewModel? Confirm { get; }
    public OracleActionViewModel? Edit { get; }
    public OracleActionViewModel? Cancel { get; }
    public string? Trace { get; }

    /// <summary>A preview was confirmed or cancelled: its buttons go away.</summary>
    [ObservableProperty] private bool _isResolved;
    [ObservableProperty] private string? _resolution;

    public void RefreshPrivacy()
    {
        OnPropertyChanged(nameof(Headline));
        foreach (var i in Items) i.RefreshPrivacy();
        foreach (var m in Metrics) m.RefreshPrivacy();
    }
}

/// <summary>
/// The Oracle console: shared by the Oracle page (transcript, context, history, diagnostics) and the Ctrl+Space overlay
/// (latest result). Keyboard-first; every action goes through existing services and navigation.
/// </summary>
public sealed partial class OracleConsoleViewModel : ObservableObject, IActivatable
{
    /// <summary>QA runs (ARCADIA_QA_ORACLE) log each command's timing at Information so it reaches the log file.</summary>
    private static readonly bool QaTiming = Environment.GetEnvironmentVariable("ARCADIA_QA_ORACLE") is { Length: > 0 };
    private readonly IServiceProvider _services;
    private readonly OracleHost _host;
    private readonly ILocalizer _loc;
    private readonly IMessenger _messenger;
    private readonly ILogger<OracleConsoleViewModel> _logger;
    private int _historyIndex = -1;
    private CancellationTokenSource? _running;

    public OracleConsoleViewModel(IServiceProvider services, OracleHost host, ILocalizer loc, IMessenger messenger, ILogger<OracleConsoleViewModel> logger)
    {
        _services = services;
        _host = host;
        _loc = loc;
        _messenger = messenger;
        _logger = logger;
        FinancePrivacy.Instance.PropertyChanged += (_, _) => { foreach (var e in Transcript) e.RefreshPrivacy(); };
    }

    private ShellViewModel Shell => _services.GetRequiredService<ShellViewModel>();

    public ObservableCollection<OracleEntryViewModel> Transcript { get; } = [];
    public ObservableCollection<string> Recent { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public ObservableCollection<string> UnknownCommands { get; } = [];

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private bool _isOverlayOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private OracleEntryViewModel? _latest;
    [ObservableProperty] private string _contextText = string.Empty;
    [ObservableProperty] private string _languageText = string.Empty;

    public bool IsEnabled => _host.Prefs.Enabled;
    public bool ShowDiagnostics => _host.Prefs.DeveloperDiagnostics;
    public bool ShowSuggestions => _host.Prefs.ShowSuggestions;
    public bool CollectUnknown => _host.Prefs.CollectUnknown;
    public string Shortcut => _host.Prefs.Shortcut;

    /// <summary>Asks the view to focus the input (overlay opened, Edit pressed).</summary>
    public event EventHandler? FocusRequested;

    public async Task ActivateAsync()
    {
        await _host.LoadAsync();
        RefreshSide();
        OnPropertyChanged(nameof(ShowDiagnostics));
        OnPropertyChanged(nameof(ShowSuggestions));
        OnPropertyChanged(nameof(CollectUnknown));
        OnPropertyChanged(nameof(IsEnabled));
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task OpenOverlayAsync()
    {
        if (!_host.Prefs.Enabled) return;
        if (IsOverlayOpen) { CloseOverlay(); return; }
        await _host.LoadAsync();
        RefreshSide();
        IsOverlayOpen = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void CloseOverlay()
    {
        _running?.Cancel();
        IsOverlayOpen = false;
        _historyIndex = -1;
    }

    /// <summary>Context panel and suggestions for the page the user is on.</summary>
    private void RefreshSide()
    {
        var module = Shell.CurrentKey is { Length: > 0 } k ? k : "home";
        Suggestions.Clear();
        if (_host.IsCreated || IsOverlayOpen || Shell.CurrentKey == Sections.Oracle)
            foreach (var s in _host.Engine.SuggestionsFor(module == Sections.Oracle ? "home" : module, UiLanguage, 6)) Suggestions.Add(s);
        Recent.Clear();
        foreach (var h in _host.History.AsEnumerable().Reverse().Take(12)) Recent.Add(h.Text);
        UnknownCommands.Clear();
        foreach (var u in _host.Unknown.AsEnumerable().Reverse().Take(30)) UnknownCommands.Add(u.Text);
        var ctx = _host.Context;
        ContextText = _loc.Format("Orc_ContextLine", _loc["Orc_Section_" + module], ctx.CurrentName ?? "—", ctx.FocusName ?? "—");
        LanguageText = _host.ForcedLanguage is { } l ? _loc["Orc_Lang_" + l] : _loc["Orc_Lang_Auto"];
    }

    private OracleLanguage UiLanguage => _loc.Culture.TwoLetterISOLanguageName == "ar" ? OracleLanguage.Arabic : OracleLanguage.English;

    [RelayCommand]
    private Task RunAsync() => RunTextAsync(Input);

    [RelayCommand]
    public async Task RunTextAsync(string? text)
    {
        text = (text ?? string.Empty).Trim();
        if (IsBusy) return;
        IsBusy = true;
        _running = new CancellationTokenSource();
        try
        {
            await _host.LoadAsync();
            await UpdateContextAsync();
            var result = await _host.Engine.RunAsync(new OracleRequest(text, _host.ForcedLanguage), _host.Context, _running.Token);
            // handler, intent and timing only — never the typed text (it may contain notes or amounts)
            if (result.Trace is { } t) _logger.Log(QaTiming ? LogLevel.Information : LogLevel.Debug, "Oracle {Handler} {Intent} {Confidence} {Ms} ms", t.Handler, t.Intent, t.Confidence, t.TotalMs);
            var entry = Add(text, result);
            if (text.Length > 0) _host.Record(text, result.Trace?.Handler, result.Kind == OracleResultKind.Unknown);
            Input = string.Empty;
            _historyIndex = -1;
            if (result.Kind == OracleResultKind.Done) Broadcast();
            if (result.AutoAction is { } auto) await ExecuteAsync(new OracleActionViewModel(auto, entry));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Oracle command failed");
            Add(text, new OracleResult { Kind = OracleResultKind.Error, Headline = _loc["Orc_Failed"], Detail = ex.Message, Confidence = OracleConfidence.Low });
        }
        finally
        {
            IsBusy = false;
            RefreshSide();
        }
    }

    private OracleEntryViewModel Add(string input, OracleResult result)
    {
        var entry = new OracleEntryViewModel(input, result, _loc, ShowDiagnostics);
        Transcript.Add(entry);
        while (Transcript.Count > 40) Transcript.RemoveAt(0);
        Latest = entry;
        return entry;
    }

    /// <summary>Where the user is: the section and the record its page shows.</summary>
    private async Task UpdateContextAsync()
    {
        var ctx = _host.Context;
        var shell = Shell;
        var module = shell.CurrentKey is { Length: > 0 } k ? k : "home";
        var current = shell.PageContext;
        if (ctx.Module != module || ctx.Current != current)
        {
            ctx.Module = module;
            ctx.Current = current;
            ctx.CurrentName = null;
            if (current is { } c)
            {
                var names = await _services.GetRequiredService<IEntityDirectory>().DescribeAsync([c]);
                ctx.CurrentName = names.TryGetValue(c, out var info) ? info.Name : null;
            }
        }
    }

    /// <summary>After a change made through Oracle, pages and Oracle's name index refresh.</summary>
    private void Broadcast()
    {
        _host.InvalidateAll();
        _messenger.Send(new OrganizerChangedMessage(OrganizerChange.All));
        _messenger.Send(new LibraryChangedMessage());
        _messenger.Send(new ProjectsChangedMessage());
        _messenger.Send(new TravelChangedMessage());
        _messenger.Send(new DocumentsChangedMessage());
    }

    [RelayCommand]
    public async Task ExecuteAsync(OracleActionViewModel? vm)
    {
        if (vm == null || !vm.IsEnabled) return;
        var a = vm.Action;
        try
        {
            switch (a.Kind)
            {
                case OracleActionKind.Confirm or OracleActionKind.Invoke when a.Invoke != null:
                {
                    if (vm.Owner.IsResolved && a.Kind == OracleActionKind.Confirm) return;
                    vm.Owner.IsResolved = a.Kind == OracleActionKind.Confirm || vm.Owner.IsResolved;
                    if (a.Kind == OracleActionKind.Confirm) vm.Owner.Resolution = _loc["Orc_Confirmed"];
                    var result = await a.Invoke(CancellationToken.None);
                    Add("› " + a.Label, result);
                    Broadcast();
                    break;
                }
                case OracleActionKind.Cancel:
                    vm.Owner.IsResolved = true;
                    vm.Owner.Resolution = _loc["Orc_Cancelled"];
                    break;
                case OracleActionKind.Edit:
                    Input = a.Command ?? vm.Owner.Input;
                    FocusRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case OracleActionKind.Run when a.Command != null:
                    await RunTextAsync(a.Command);
                    break;
                case OracleActionKind.Open when a.Target is { } target:
                    CloseOverlay();
                    await Shell.OpenAsync(target, a.Tab);
                    break;
                case OracleActionKind.Navigate when a.Section != null:
                    CloseOverlay();
                    await Shell.NavigateToAsync(a.Section, a.Tab);
                    break;
                case OracleActionKind.Copy when a.Text != null:
                    if (_services.GetRequiredService<IClipboardService>().SetText(a.Text))
                        _services.GetRequiredService<Services.INotificationService>().Show(_loc["Orc_Copied"], NotificationKind.Success);
                    break;
                case OracleActionKind.OpenFile when a.Text != null:
                    CloseOverlay();
                    if (a.Target is { Kind: EntityKind.Document } doc) await _services.GetRequiredService<DocumentActions>().OpenAsync(doc.Id, a.Text);
                    else Process.Start(new ProcessStartInfo(a.Text) { UseShellExecute = true })?.Dispose();
                    break;
                case OracleActionKind.OpenFolder when a.Text != null:
                    _services.GetRequiredService<DocumentActions>().Reveal(a.Text);
                    break;
                case OracleActionKind.EditAutomation when a.Payload is Arcadia.Core.Automation.AutomationDefinition def:
                {
                    CloseOverlay();
                    vm.Owner.IsResolved = true;
                    vm.Owner.Resolution = _loc["Orc_OpenedInEditor"];
                    Shell.Navigate(Sections.Automation);
                    var auto = _services.GetRequiredService<Automation.AutomationViewModel>();
                    await auto.ActivateAsync();
                    auto.Tab = Automation.AutomationTab.Automations;
                    auto.Selected = null;
                    auto.Edit(def);
                    break;
                }
                case OracleActionKind.EditTransaction when a.Payload is TransactionDraft draft:
                    CloseOverlay();
                    vm.Owner.IsResolved = true;
                    vm.Owner.Resolution = _loc["Orc_OpenedInEditor"];
                    await _services.GetRequiredService<Finance.FinanceActions>().NewTransactionAsync(TransactionType.Expense, draft);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Oracle action {Kind} failed", a.Kind);
            Add("› " + a.Label, new OracleResult { Kind = OracleResultKind.Error, Headline = _loc["Orc_Failed"], Detail = ex.Message, Confidence = OracleConfidence.Low });
        }
    }

    // ── history (Up/Down in the input) ──

    [RelayCommand]
    private void HistoryUp()
    {
        var h = _host.History;
        if (h.Count == 0) return;
        _historyIndex = _historyIndex < 0 ? h.Count - 1 : Math.Max(0, _historyIndex - 1);
        Input = h[_historyIndex].Text;
    }

    [RelayCommand]
    private void HistoryDown()
    {
        var h = _host.History;
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= h.Count) { _historyIndex = -1; Input = string.Empty; return; }
        Input = h[_historyIndex].Text;
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _host.ClearHistoryAsync();
        RefreshSide();
    }

    [RelayCommand]
    private async Task ClearUnknownAsync()
    {
        await _host.ClearUnknownAsync();
        RefreshSide();
    }

    [RelayCommand]
    private void ClearTranscript()
    {
        Transcript.Clear();
        Latest = null;
    }

    [RelayCommand]
    private void ResetContext()
    {
        _host.Context.Reset();
        RefreshSide();
    }

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    public void SettingsChanged()
    {
        OnPropertyChanged(nameof(ShowDiagnostics));
        OnPropertyChanged(nameof(ShowSuggestions));
        OnPropertyChanged(nameof(CollectUnknown));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Shortcut));
    }
}
