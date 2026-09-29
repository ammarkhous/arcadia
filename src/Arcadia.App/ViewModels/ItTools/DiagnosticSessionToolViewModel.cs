using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.ItTools;

/// <summary>An entry in a session timeline: a recorded tool result or a note.</summary>
public sealed partial class SessionEntryViewModel : ObservableObject
{
    public int Id { get; init; }
    public bool IsNote { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string Time { get; init; } = string.Empty;
    public string Icon { get; init; } = "Journal";
    public string Title { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string Tone { get; init; } = "Blue";
    /// <summary>The full result as plain text (results) or the note exactly as typed.</summary>
    public string Body { get; init; } = string.Empty;
    [ObservableProperty] private bool _isExpanded;
}

public sealed partial class DiagnosticSessionToolViewModel(ToolContext context, IDiagnosticSessionStore store) : ToolViewModel(context)
{
    private DiagnosticSession? _session;
    private bool _loadedOnce;

    public override string Key => "session";
    public override string Icon => "Diagnostic";
    public override ToolCategory Category => ToolCategory.Diagnostics;

    public DiagnosticSessionContext Active => Context.Sessions;
    public ObservableCollection<DiagnosticSessionSummary> SessionList { get; } = [];
    public ObservableCollection<SessionEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private DiagnosticSessionSummary? _selectedSummary;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSession))] private string? _sessionTitle;
    [ObservableProperty] private string? _reference;
    [ObservableProperty] private string _started = string.Empty;
    [ObservableProperty] private string _state = string.Empty;
    [ObservableProperty] private string _stateTone = "Steel";
    [ObservableProperty] private bool _isClosed;
    [ObservableProperty] private bool _isActiveSession;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddNoteCommand))] private string _noteText = string.Empty;
    [ObservableProperty] private string? _noteError;

    public bool HasSession => _session != null;
    public int NoteMaxLength => 8000;

    public override async Task OnShownAsync()
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            Context.Sessions.Changed += async (_, _) => await ReloadAsync(keepSelection: true);
            Context.Sessions.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DiagnosticSessionContext.ActiveId)) IsActiveSession = _session != null && Context.Sessions.ActiveId == _session.Id;
            };
        }
        await Context.Sessions.EnsureLoadedAsync();
        await ReloadAsync(keepSelection: true);
        if (_session == null && (Context.Sessions.ActiveId ?? SessionList.FirstOrDefault()?.Id) is { } id) await SelectAsync(id);
    }

    private async Task ReloadAsync(bool keepSelection)
    {
        try
        {
            var list = await store.ListAsync();
            var keep = keepSelection ? _session?.Id : null;
            SessionList.Clear();
            foreach (var s in list) SessionList.Add(s);
            if (keep is { } id)
            {
                if (SessionList.Any(s => s.Id == id)) await SelectAsync(id);
                else ClearDetail();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    partial void OnSelectedSummaryChanged(DiagnosticSessionSummary? value)
    {
        if (value != null && value.Id != _session?.Id) _ = SelectAsync(value.Id);
    }

    public async Task SelectAsync(int id)
    {
        try
        {
            var session = await store.GetAsync(id);
            if (session == null) { ClearDetail(); return; }
            _session = session;
            SessionTitle = session.Title;
            Reference = session.Reference;
            Started = LocalTime(session.CreatedAtUtc);
            IsClosed = session.IsClosed;
            IsActiveSession = Context.Sessions.ActiveId == session.Id;
            State = session.IsClosed ? Loc.Format("Diag_ClosedAt", LocalTime(session.ClosedAtUtc)) : IsActiveSession ? Loc["Diag_Recording"] : Loc["Diag_Open"];
            StateTone = session.IsClosed ? "Steel" : IsActiveSession ? "Red" : "Green";
            SelectedSummary = SessionList.FirstOrDefault(s => s.Id == id);
            BuildEntries(session);
            HasResult = true;
            OnPropertyChanged(nameof(HasSession));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ClearDetail()
    {
        _session = null;
        SessionTitle = null;
        Entries.Clear();
        HasResult = false;
        OnPropertyChanged(nameof(HasSession));
    }

    private void BuildEntries(DiagnosticSession session)
    {
        var tools = new Dictionary<string, string>
        {
            ["sysinfo"] = "Monitor", ["performance"] = "Cpu", ["network"] = "Network", ["ping"] = "Ping", ["trace"] = "Route", ["dns"] = "Dns",
            ["port"] = "Port", ["devices"] = "Devices", ["storage"] = "Drive", ["largefiles"] = "FileSearch", ["processes"] = "Processes",
            ["services"] = "Services", ["eventlog"] = "EventLog", ["hash"] = "Hash", ["subnet"] = "Subnet",
        };
        var items = session.Results.Select(r => new SessionEntryViewModel
        {
            Id = r.Id,
            CreatedUtc = r.CreatedAtUtc,
            Time = LocalTime(r.CreatedAtUtc),
            Icon = tools.GetValueOrDefault(r.Tool, "Diagnostic"),
            Title = r.Title,
            Summary = r.Summary,
            Tone = r.Outcome switch { DiagnosticOutcome.Success => "Green", DiagnosticOutcome.Warning => "Amber", DiagnosticOutcome.Failure => "Red", _ => "Blue" },
            Body = JsonReportExporter.DeserializeSection(r.SectionJson) is { } section ? TextReportExporter.Render(section) : string.Empty,
        }).Concat(session.Notes.Select(n => new SessionEntryViewModel
        {
            Id = n.Id,
            IsNote = true,
            CreatedUtc = n.CreatedAtUtc,
            Time = LocalTime(n.CreatedAtUtc),
            Icon = "Journal",
            Title = Loc["Diag_Note"],
            Tone = "Gold",
            Body = n.Text,
        })).OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id);
        Entries.Clear();
        foreach (var e in items) Entries.Add(e);
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var editor = new DiagnosticSessionEditorViewModel(Loc, null, null);
        if (!Context.Dialogs.Show(editor)) return;
        var session = await Context.Sessions.StartAsync(editor.SessionTitle.Trim(), editor.Reference);
        await ReloadAsync(keepSelection: false);
        await SelectAsync(session.Id);
    }

    [RelayCommand]
    private async Task EditDetailsAsync()
    {
        if (_session == null) return;
        var editor = new DiagnosticSessionEditorViewModel(Loc, _session.Title, _session.Reference);
        if (!Context.Dialogs.Show(editor)) return;
        await store.UpdateDetailsAsync(_session.Id, editor.SessionTitle, editor.Reference);
        if (Context.Sessions.ActiveId == _session.Id) Context.Sessions.SetActive(_session.Id, editor.SessionTitle.Trim());
        await ReloadAsync(keepSelection: true);
    }

    [RelayCommand]
    private async Task MakeActiveAsync()
    {
        if (_session == null) return;
        if (_session.IsClosed) await store.SetClosedAsync(_session.Id, false);
        Context.Sessions.SetActive(_session.Id, _session.Title);
        await ReloadAsync(keepSelection: true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        Context.Sessions.ClearActive();
        if (_session != null) _ = SelectAsync(_session.Id);
    }

    [RelayCommand]
    private async Task ToggleClosedAsync()
    {
        if (_session == null) return;
        var close = !_session.IsClosed;
        await store.SetClosedAsync(_session.Id, close);
        if (close && Context.Sessions.ActiveId == _session.Id) Context.Sessions.ClearActive();
        await ReloadAsync(keepSelection: true);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync()
    {
        if (_session == null) return;
        if (!Context.Dialogs.Confirm(Loc.Format("Diag_ConfirmDelete", _session.Title, Entries.Count), Loc["Diag_Delete"])) return;
        var id = _session.Id;
        await store.DeleteAsync(id);
        if (Context.Sessions.ActiveId == id) Context.Sessions.ClearActive();
        ClearDetail();
        await ReloadAsync(keepSelection: false);
        Context.Sessions.NotifyChanged();
    }

    private bool CanAddNote() => !string.IsNullOrWhiteSpace(NoteText);

    [RelayCommand(CanExecute = nameof(CanAddNote))]
    private async Task AddNoteAsync()
    {
        if (_session == null) return;
        NoteError = null;
        if (NoteText.Length > NoteMaxLength) { NoteError = Loc.Format("Diag_ErrNoteLength", NoteMaxLength); return; }
        await store.AddNoteAsync(_session.Id, NoteText);
        NoteText = string.Empty;
        await SelectAsync(_session.Id);
        Context.Sessions.NotifyChanged();
    }

    [RelayCommand]
    private async Task DeleteEntryAsync(SessionEntryViewModel? entry)
    {
        if (entry == null || _session == null) return;
        if (!Context.Dialogs.Confirm(Loc.Format(entry.IsNote ? "Diag_ConfirmDeleteNote" : "Diag_ConfirmDeleteResult", entry.Title), Loc["Diag_Delete"])) return;
        if (entry.IsNote) await store.DeleteNoteAsync(entry.Id);
        else await store.DeleteResultAsync(entry.Id);
        await SelectAsync(_session.Id);
    }

    [RelayCommand]
    private void CopyEntry(SessionEntryViewModel? entry)
    {
        if (entry != null) Copy(entry.Body, entry.Title);
    }

    [RelayCommand]
    private void ToggleEntry(SessionEntryViewModel? entry)
    {
        if (entry != null) entry.IsExpanded = !entry.IsExpanded;
    }

    private ReportDocument? BuildDocument()
    {
        if (_session == null) return null;
        var labels = new DiagnosticReport.Labels(Loc["Diag_Session"], Loc["Diag_Reference"], Loc["Diag_Computer"], Loc["Diag_Started"], Loc["Diag_Closed"],
            Loc["Diag_StillOpen"], Loc["Diag_Note"], Loc["It_Result"], Loc["Diag_Entries"]);
        return DiagnosticReport.Build(_session, labels, "Arcadia", Context.Time.GetUtcNow().UtcDateTime);
    }

    [RelayCommand]
    private async Task ExportSessionAsync()
    {
        if (BuildDocument() is { } doc && _session != null)
            await Context.Exports.ExportAsync(doc, $"{_session.Title} {_session.CreatedAtUtc.ToLocalTime():yyyy-MM-dd}");
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (BuildDocument() is { } doc) Copy(TextReportExporter.Render(doc));
    }

    public override bool SupportsReport => false;
    public override ReportSection? BuildReport() => null; // sessions export whole documents instead
}
