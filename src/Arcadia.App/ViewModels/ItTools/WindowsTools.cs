using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using Arcadia.App.Services;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Reports;
using Arcadia.Core.ItTools.SystemInfo;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.ItTools;

// ═══════════════════════════ Processes ═══════════════════════════

/// <summary>A process row updated in place between refreshes, so sorting and selection survive.</summary>
public sealed partial class ProcessRow(int id, string name) : ObservableObject
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private long _memory;
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _started = "—";
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private bool _isCritical;
    [ObservableProperty] private int _threads;
    public DateTime? StartedUtc { get; set; }
}

public sealed partial class ProcessesToolViewModel : ToolViewModel
{
    private readonly IProcessService _processes;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<(int, DateTime?), ProcessRow> _byKey = [];
    private bool _refreshing;

    public ProcessesToolViewModel(ToolContext context, IProcessService processes) : base(context)
    {
        _processes = processes;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ProcessRow r && Matches(r);
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Memory), ListSortDirection.Descending));
        if (View is ICollectionViewLiveShaping live)
        {
            live.IsLiveSorting = true;
            live.IsLiveFiltering = false;
        }
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshCoreAsync(silent: true);
    }

    public override string Key => "processes";
    public override string Icon => "Processes";
    public override ToolCategory Category => ToolCategory.Processes;

    public ObservableCollection<ProcessRow> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EndProcessCommand), nameof(RevealCommand))] private ProcessRow? _selected;
    [ObservableProperty] private bool _isLive = true;

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnIsLiveChanged(bool value)
    {
        if (value) _timer.Start();
        else _timer.Stop();
    }

    private bool Matches(ProcessRow r) =>
        string.IsNullOrWhiteSpace(Search)
        || r.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || r.Id.ToString(CultureInfo.InvariantCulture) == Search.Trim()
        || r.Path.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public override async Task OnShownAsync()
    {
        await RefreshCoreAsync(silent: Rows.Count > 0);
        if (IsLive) _timer.Start();
    }

    public override void OnHidden() => _timer.Stop();

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(silent: false);

    private async Task RefreshCoreAsync(bool silent)
    {
        if (_refreshing) return;
        if (System.Windows.Application.Current?.MainWindow?.WindowState == System.Windows.WindowState.Minimized) return;
        _refreshing = true;
        try
        {
            if (!silent) IsBusy = true;
            var list = await _processes.GetProcessesAsync();
            var seen = new HashSet<(int, DateTime?)>();
            foreach (var p in list)
            {
                var key = (p.Id, p.StartedAtUtc);
                seen.Add(key);
                if (!_byKey.TryGetValue(key, out var row))
                {
                    row = new ProcessRow(p.Id, p.Name) { StartedUtc = p.StartedAtUtc, Started = LocalTime(p.StartedAtUtc), IsCritical = p.IsCritical };
                    _byKey[key] = row;
                    Rows.Add(row);
                }
                row.Cpu = p.CpuPercent ?? 0;
                row.CpuText = p.CpuPercent is { } c ? c.ToString(c < 10 ? "0.0" : "0", Culture) : "—";
                row.Memory = p.WorkingSetBytes;
                row.MemoryText = ByteSize.Format(p.WorkingSetBytes, Culture);
                row.Path = p.Path ?? string.Empty;
                row.Threads = p.Threads;
            }
            foreach (var gone in _byKey.Where(k => !seen.Contains(k.Key)).ToList())
            {
                _byKey.Remove(gone.Key);
                Rows.Remove(gone.Value);
            }
            HasResult = Rows.Count > 0;
            StatusText = Loc.Format("Proc_Count", Rows.Count, ByteSize.Format(list.Sum(p => p.WorkingSetBytes), Culture));
            Error = null;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _refreshing = false;
            IsBusy = false;
        }
    }

    private bool HasSelection() => Selected != null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal()
    {
        if (Selected is { Path.Length: > 0 } r) Context.Explorer.Reveal(r.Path);
        else Context.Notifications.Show(Loc["Proc_NoPath"], NotificationKind.Warning);
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is { } r) Copy(Describe(r), r.Name);
    }

    private string Describe(ProcessRow r) =>
        $"{r.Name} (PID {r.Id})\n{Loc["Proc_Cpu"]}: {r.CpuText}%  {Loc["Proc_Memory"]}: {r.MemoryText}  {Loc["Proc_Threads"]}: {r.Threads}\n{Loc["Proc_Started"]}: {r.Started}\n{Loc["Proc_Path"]}: {(r.Path.Length > 0 ? r.Path : "—")}";

    /// <summary>Ends a process only after explicit confirmation; system-critical processes get a strong warning.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EndProcessAsync()
    {
        if (Selected is not { } r) return;
        var message = r.IsCritical
            ? Loc.Format("Proc_ConfirmCritical", r.Name, r.Id)
            : Loc.Format("Proc_Confirm", r.Name, r.Id);
        if (!Context.Dialogs.Confirm(message, Loc["Proc_End"])) return;

        var outcome = await _processes.TerminateAsync(r.Id);
        if (outcome == ProcessTerminateOutcome.AccessDenied)
        {
            if (!Context.Dialogs.Confirm(Loc.Format("Proc_NeedsAdmin", r.Name), Loc["It_ContinueAsAdmin"])) return;
            outcome = await _processes.TerminateElevatedAsync(r.Id) ? ProcessTerminateOutcome.Terminated : ProcessTerminateOutcome.Failed;
        }
        switch (outcome)
        {
            case ProcessTerminateOutcome.Terminated:
                Context.Notifications.Show(Loc.Format("Proc_Ended", r.Name), NotificationKind.Success);
                break;
            case ProcessTerminateOutcome.NotFound:
                Context.Notifications.Show(Loc.Format("Proc_AlreadyGone", r.Name));
                break;
            default:
                Context.Notifications.Show(Loc.Format("Proc_EndFailed", r.Name), NotificationKind.Warning);
                break;
        }
        await RefreshCoreAsync(silent: true);
    }

    public override ReportSection? BuildReport()
    {
        if (Rows.Count == 0) return null;
        var table = new ReportTable(Loc["Proc_Name"], "PID", Loc["Proc_Cpu"], Loc["Proc_Memory"], Loc["Proc_Started"], Loc["Proc_Path"]);
        foreach (var r in View.Cast<ProcessRow>()) table.AddRow(r.Name, r.Id.ToString(CultureInfo.InvariantCulture), r.CpuText, r.MemoryText, r.Started, r.Path);
        return new ReportSection(Title) { Table = table }.Add(Loc["It_Result"], StatusText);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult()
    {
        var top = Rows.OrderByDescending(r => r.Cpu).Take(3).Select(r => $"{r.Name} {r.CpuText}%");
        return (Title, $"{StatusText} · {string.Join(", ", top)}", DiagnosticOutcome.Info);
    }
}

// ═══════════════════════════ Services ═══════════════════════════

public sealed record ServiceRow(string Name, string DisplayName, string Status, string StatusText, string StartType, string Tone, bool IsRunning, bool IsStopped, bool CanStop);

public sealed partial class ServicesToolViewModel(ToolContext context, IWindowsServiceManager manager) : ToolViewModel(context)
{
    private IReadOnlyList<ServiceEntry> _all = [];

    public override string Key => "services";
    public override string Icon => "Services";
    public override ToolCategory Category => ToolCategory.Windows;

    [ObservableProperty] private IReadOnlyList<ServiceRow> _services = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartServiceCommand), nameof(StopServiceCommand), nameof(RestartServiceCommand))] private ServiceRow? _selected;
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private string _search = string.Empty;

    partial void OnFilterChanged(string value) => Apply();
    partial void OnSearchChanged(string value) => Apply();

    public override async Task OnShownAsync()
    {
        if (!HasResult && !IsBusy) await RefreshAsync();
    }

    [RelayCommand]
    private void SetFilter(string? filter) => Filter = filter ?? "All";

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        _all = await manager.GetServicesAsync(ct);
        Apply();
        HasResult = _all.Count > 0;
    });

    protected override void OnCultureChanged() => Apply();

    private void Apply()
    {
        var term = Search.Trim();
        Services = _all
            .Where(s => Filter switch { "Running" => s.IsRunning, "Stopped" => s.IsStopped, _ => true })
            .Where(s => term.Length == 0 || s.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase) || s.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(s => new ServiceRow(s.Name, s.DisplayName, s.Status, Loc[$"Svc_{s.Status}"], Loc[$"SvcStart_{s.StartType}"],
                s.IsRunning ? "Green" : s.IsStopped ? "Steel" : "Amber", s.IsRunning, s.IsStopped, s.CanStop))
            .ToList();
        StatusText = Loc.Format("Svc_Count", Services.Count, _all.Count(s => s.IsRunning), _all.Count);
        Selected = Selected == null ? null : Services.FirstOrDefault(s => s.Name == Selected.Name);
    }

    private bool CanStart() => Selected is { IsStopped: true } && !IsBusy;
    private bool CanStop() => Selected is { IsRunning: true, CanStop: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartServiceAsync() => ControlAsync(ServiceAction.Start);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopServiceAsync() => ControlAsync(ServiceAction.Stop);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task RestartServiceAsync() => ControlAsync(ServiceAction.Restart);

    /// <summary>Confirm → try with current rights → if Windows refuses, explain and offer a UAC prompt.</summary>
    private async Task ControlAsync(ServiceAction action)
    {
        if (Selected is not { } s) return;
        if (!Context.Dialogs.Confirm(Loc.Format($"Svc_Confirm{action}", s.DisplayName, s.Name), Loc[$"Svc_{action}"])) return;
        await RunAsync(async ct =>
        {
            StatusText = Loc.Format("Svc_Working", Loc[$"Svc_{action}"], s.DisplayName);
            var result = await manager.ControlAsync(s.Name, action, ct);
            if (result.Outcome == ServiceControlOutcome.RequiresElevation)
            {
                if (!Context.Dialogs.Confirm(Loc.Format("Svc_NeedsAdmin", s.DisplayName), Loc["It_ContinueAsAdmin"]))
                {
                    StatusText = Loc["Svc_NotChanged"];
                    return;
                }
                result = await manager.ControlElevatedAsync(s.Name, action, ct);
            }
            switch (result.Outcome)
            {
                case ServiceControlOutcome.Done:
                    Context.Notifications.Show(Loc.Format($"Svc_Done{action}", s.DisplayName), NotificationKind.Success);
                    break;
                case ServiceControlOutcome.Declined:
                    Context.Notifications.Show(Loc["Svc_Declined"], NotificationKind.Warning);
                    break;
                default:
                    Context.Notifications.Show(Loc.Format("Svc_Failed", s.DisplayName, result.Detail ?? string.Empty), NotificationKind.Warning);
                    break;
            }
            _all = await manager.GetServicesAsync(ct);
            Apply();
        });
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is { } s) Copy($"{s.DisplayName} ({s.Name}) — {s.StatusText}, {s.StartType}", s.Name);
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var table = new ReportTable(Loc["Svc_Display"], Loc["Svc_Name"], Loc["Svc_Status"], Loc["Svc_StartType"]);
        foreach (var s in Services) table.AddRow(s.DisplayName, s.Name, s.StatusText, s.StartType);
        return new ReportSection($"{Title} ({Loc[$"Svc_Filter{Filter}"]})") { Table = table }.Add(Loc["It_Result"], StatusText);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() => (Title, StatusText, DiagnosticOutcome.Info);
}

// ═══════════════════════════ Event log ═══════════════════════════

public sealed record EventRow(DateTime Local, string Time, string Level, string Tone, string Source, int EventId, string Message, string FirstLine);

public sealed partial class EventLogToolViewModel(ToolContext context, IEventLogService events) : ToolViewModel(context)
{
    public override string Key => "eventlog";
    public override string Icon => "EventLog";
    public override ToolCategory Category => ToolCategory.Windows;

    public IReadOnlyList<string> Logs { get; } = ["Application", "System", "Security"];
    public IReadOnlyList<int> Limits { get; } = [200, 500, 1000];

    [ObservableProperty] private string _logName = "System";
    [ObservableProperty] private bool _critical = true;
    [ObservableProperty] private bool _errors = true;
    [ObservableProperty] private bool _warnings = true;
    [ObservableProperty] private bool _information;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private int _limit = 200;
    [ObservableProperty] private IReadOnlyList<EventRow> _entries = [];
    [ObservableProperty] private EventRow? _selected;

    public override async Task OnShownAsync()
    {
        if (!HasResult && !IsBusy) await LoadAsync();
    }

    [RelayCommand]
    private Task SelectLogAsync(string? log)
    {
        if (log == null || log == LogName) return Task.CompletedTask;
        LogName = log;
        return LoadAsync();
    }

    [RelayCommand]
    private Task LoadAsync()
    {
        var levels = new HashSet<EventSeverity>();
        if (Critical) levels.Add(EventSeverity.Critical);
        if (Errors) levels.Add(EventSeverity.Error);
        if (Warnings) levels.Add(EventSeverity.Warning);
        if (Information) levels.Add(EventSeverity.Information);
        if (levels.Count == 0) { StatusText = Loc["Evt_PickLevel"]; return Task.CompletedTask; }

        return RunAsync(async ct =>
        {
            Entries = [];
            HasResult = false;
            StatusText = Loc.Format("Evt_Loading", LogName);
            var list = await events.QueryAsync(new EventLogQuery(LogName, levels, Limit, string.IsNullOrWhiteSpace(Search) ? null : Search.Trim()), ct);
            Entries = list.Select(e =>
            {
                var local = e.TimeUtc.ToLocalTime();
                var first = e.Message.Split('\n', 2)[0].Trim();
                return new EventRow(local, local.ToString("g", Culture), Loc[$"Evt_{e.Level}"], e.Level switch
                {
                    EventSeverity.Critical => "Red",
                    EventSeverity.Error => "Red",
                    EventSeverity.Warning => "Amber",
                    _ => "Blue",
                }, e.Source, e.EventId, e.Message, first.Length > 220 ? first[..220] + "…" : first);
            }).ToList();
            Selected = Entries.FirstOrDefault();
            HasResult = true;
            StatusText = Loc.Format("Evt_Count", Entries.Count, LogName);
        });
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is { } e) Copy($"{e.Time}  {e.Level}  {e.Source}  ({e.EventId})\n{e.Message}", $"{e.Source} {e.EventId}");
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var table = new ReportTable(Loc["Evt_Time"], Loc["Evt_Level"], Loc["Evt_Source"], Loc["Evt_Id"], Loc["Evt_Message"]);
        foreach (var e in Entries) table.AddRow(e.Time, e.Level, e.Source, e.EventId.ToString(CultureInfo.InvariantCulture), e.FirstLine);
        return new ReportSection($"{Title}: {LogName}") { Table = table }.Add(Loc["It_Result"], StatusText);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        ($"{Title} {LogName}", StatusText, Entries.Any(e => e.Tone == "Red") ? DiagnosticOutcome.Warning : DiagnosticOutcome.Info);
}

// ═══════════════════════════ Quick commands ═══════════════════════════

public sealed partial class QuickCommandItem(QuickCommand command) : ObservableObject
{
    public QuickCommand Command { get; } = command;
    public string Icon => Command.Icon;
    public bool NeedsAdmin => Command.RunAsAdministrator || Command.PromptsForAdministrator;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
}

public sealed partial class QuickCommandsToolViewModel : ToolViewModel
{
    private readonly IQuickCommandLauncher _launcher;

    public QuickCommandsToolViewModel(ToolContext context, IQuickCommandLauncher launcher) : base(context)
    {
        _launcher = launcher;
        Commands = QuickCommands.All.Select(c => new QuickCommandItem(c)).ToList();
        OnCultureChanged();
    }

    public override string Key => "commands";
    public override string Icon => "Terminal";
    public override ToolCategory Category => ToolCategory.Windows;

    public IReadOnlyList<QuickCommandItem> Commands { get; }

    protected override void OnCultureChanged()
    {
        foreach (var c in Commands)
        {
            c.Title = Loc[$"QuickCmd_{c.Command.Key}"];
            c.Description = Loc[$"QuickCmd_{c.Command.Key}_Desc"];
        }
    }

    [RelayCommand]
    private void Launch(QuickCommandItem? item)
    {
        if (item == null) return;
        if (item.Command.RunAsAdministrator && !Context.Dialogs.Confirm(Loc.Format("QuickCmd_ConfirmAdmin", item.Title), Loc["It_ContinueAsAdmin"])) return;
        if (_launcher.Launch(item.Command)) StatusText = Loc.Format("QuickCmd_Started", item.Title);
        else Context.Notifications.Show(Loc.Format("QuickCmd_Failed", item.Title), NotificationKind.Warning);
    }

    public override bool SupportsReport => false;
    public override ReportSection? BuildReport() => null;
}

// ═══════════════════════════ Hash calculator ═══════════════════════════

public sealed partial class HashToolViewModel(ToolContext context, IHashService hashes) : ToolViewModel(context)
{
    public override string Key => "hash";
    public override string Icon => "Hash";
    public override ToolCategory Category => ToolCategory.Utilities;

    public IReadOnlyList<HashAlgorithmKind> Algorithms { get; } = [HashAlgorithmKind.Sha256, HashAlgorithmKind.Sha512, HashAlgorithmKind.Md5];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ComputeCommand))] [NotifyPropertyChangedFor(nameof(FilePathDisplay))] private string? _filePath;

    public string FilePathDisplay => FilePath ?? Loc["Hash_NoFile"];
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _fileSize = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMd5))] private HashAlgorithmKind _algorithm = HashAlgorithmKind.Sha256;
    [ObservableProperty] private string _hash = string.Empty;
    [ObservableProperty] private string _hashAlgorithmName = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _expected = string.Empty;
    [ObservableProperty] private string _compareText = string.Empty;
    [ObservableProperty] private string _compareTone = "Steel";

    public bool IsMd5 => Algorithm == HashAlgorithmKind.Md5;

    partial void OnExpectedChanged(string value) => UpdateComparison();

    [RelayCommand]
    private void SetAlgorithm(HashAlgorithmKind kind)
    {
        if (Algorithm == kind) return;
        Algorithm = kind;
        if (FilePath != null && HasResult) _ = ComputeAsync();
    }

    [RelayCommand]
    private void ChooseFile()
    {
        var path = Context.Dialogs.PickFile(Loc["Hash_AnyFile"] + " (*.*)|*.*");
        if (path != null) SetFile(path);
    }

    /// <summary>Also used by drag and drop.</summary>
    public void SetFile(string path)
    {
        if (!File.Exists(path)) return;
        FilePath = path;
        FileName = Path.GetFileName(path);
        FileSize = ByteSize.Format(new FileInfo(path).Length, Culture);
        Hash = string.Empty;
        HasResult = false;
        _ = ComputeAsync();
    }

    private bool CanCompute() => FilePath != null;

    [RelayCommand(CanExecute = nameof(CanCompute))]
    private Task ComputeAsync() => RunAsync(async ct =>
    {
        Hash = string.Empty;
        Progress = 0;
        StatusText = Loc["Hash_Working"];
        var result = await hashes.ComputeAsync(FilePath!, Algorithm, new Progress<double>(p => Progress = p), ct);
        Hash = result.Hash;
        HashAlgorithmName = Name(result.Algorithm);
        FileSize = ByteSize.Format(result.SizeBytes, Culture);
        HasResult = true;
        var seconds = Math.Max(0.001, result.Elapsed.TotalSeconds);
        StatusText = Loc.Format("Hash_Done", result.Elapsed.TotalSeconds.ToString("0.0", Culture), ByteSize.FormatRate(result.SizeBytes / seconds, Culture));
        UpdateComparison();
    });

    public static string Name(HashAlgorithmKind k) => k switch { HashAlgorithmKind.Sha512 => "SHA-512", HashAlgorithmKind.Md5 => "MD5", _ => "SHA-256" };

    private void UpdateComparison()
    {
        if (Hash.Length == 0) { CompareText = string.Empty; CompareTone = "Steel"; return; }
        var result = HashText.Compare(Hash, Expected);
        CompareText = Loc[$"Hash_Cmp{result}"];
        CompareTone = result switch { HashComparison.Match => "Green", HashComparison.Mismatch or HashComparison.DifferentLength => "Red", HashComparison.NotAHash => "Amber", _ => "Steel" };
    }

    [RelayCommand]
    private void CopyHash() => Copy(Hash, HashAlgorithmName);

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var s = new ReportSection(Title).Add(Loc["Hash_File"], FilePath).Add(Loc["Hash_Size"], FileSize).Add(Loc["Hash_Algorithm"], HashAlgorithmName).Add(Loc["Hash_Value"], Hash);
        if (!string.IsNullOrWhiteSpace(Expected)) s.Add(Loc["Hash_Expected"], HashText.Normalize(Expected)).Add(Loc["Hash_Comparison"], CompareText);
        if (IsMd5) s.Add(Loc["Hash_Note"], Loc["Hash_Md5Warning"]);
        return s;
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        ($"{Title} {FileName}", $"{HashAlgorithmName} {Hash[..Math.Min(16, Hash.Length)]}… {CompareText}".Trim(),
            CompareTone switch { "Green" => DiagnosticOutcome.Success, "Red" => DiagnosticOutcome.Failure, _ => DiagnosticOutcome.Info });
}
