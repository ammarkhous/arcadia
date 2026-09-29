using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Reports;
using Arcadia.Core.ItTools.SystemInfo;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.ItTools;

// ═══════════════════════════ System information ═══════════════════════════

public sealed partial class SystemInfoToolViewModel(ToolContext context, ISystemInformationService system) : ToolViewModel(context)
{
    private SystemSnapshot? _snapshot;

    public override string Key => "sysinfo";
    public override string Icon => "Monitor";
    public override ToolCategory Category => ToolCategory.System;

    [ObservableProperty] private IReadOnlyList<InfoGroup> _groups = [];
    [ObservableProperty] private string _headline = string.Empty;
    [ObservableProperty] private string _subline = string.Empty;

    public override async Task OnShownAsync()
    {
        if (_snapshot == null && !IsBusy) await RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        StatusText = Loc["It_Collecting"];
        _snapshot = await system.GetSnapshotAsync(ct);
        Build();
        HasResult = true;
        StatusText = Loc.Format("It_CollectedAt", DateTime.Now.ToString("T", Culture));
    });

    protected override void OnCultureChanged()
    {
        if (_snapshot != null) Build();
    }

    private void Build()
    {
        if (_snapshot is not { } s) return;
        Headline = s.ComputerName;
        Subline = $"{s.Manufacturer} {s.Model}".Trim() is { Length: > 0 } hw ? $"{s.WindowsEdition} · {hw}" : s.WindowsEdition;
        Groups = BuildGroups(s);
    }

    private IReadOnlyList<InfoGroup> BuildGroups(SystemSnapshot s)
    {
        string L(string key) => Loc[$"Sys_{key}"];
        var groups = new List<InfoGroup>
        {
            new(L("Computer"), "Monitor",
            [
                new(L("ComputerName"), s.ComputerName),
                new(L("User"), s.UserName),
                new(L("Manufacturer"), s.Manufacturer ?? "—"),
                new(L("Model"), s.Model ?? "—"),
            ]),
            new(L("Windows"), "WindowsLogo",
            [
                new(L("Edition"), s.WindowsEdition),
                new(L("Version"), s.WindowsDisplayVersion ?? "—"),
                new(L("Build"), s.BuildNumber),
                new(L("Architecture"), s.Architecture),
                new(L("Installed"), s.InstalledAtUtc is { } i ? LocalTime(i) : "—"),
                new(L("LastBoot"), LocalTime(s.LastBootUtc)),
                new(L("Uptime"), ByteSize.FormatUptime(s.Uptime, Loc["Unit_DayShort"])),
            ]),
            new(L("Firmware"), "Shield",
            [
                new(L("FirmwareType"), s.Firmware.Type),
                new(L("SecureBoot"), s.Firmware.SecureBoot switch { true => Loc["It_On"], false => Loc["It_Off"], _ => "—" }, s.Firmware.SecureBoot == true ? "Green" : s.Firmware.SecureBoot == false ? "Amber" : null),
                new(L("FirmwareVendor"), s.Firmware.Vendor ?? "—"),
                new(L("FirmwareVersion"), s.Firmware.Version ?? "—"),
                new(L("FirmwareDate"), s.Firmware.ReleaseDate?.ToString("d", Culture) ?? "—"),
            ]),
            new(L("Processor"), "Cpu",
            [
                new(L("Cpu"), s.Cpu.Name),
                new(L("Cores"), s.Cpu.PhysicalCores.ToString(Culture)),
                new(L("Logical"), s.Cpu.LogicalProcessors.ToString(Culture)),
                new(L("Clock"), s.Cpu.MaxClockMhz is { } mhz ? Loc.Format("Sys_Mhz", mhz) : "—"),
                new(L("Memory"), ByteSize.Format(s.InstalledMemoryBytes, Culture)),
            ]),
        };
        var graphics = s.Gpus.SelectMany((g, i) => new InfoRow[]
        {
            new(s.Gpus.Count > 1 ? $"{L("Gpu")} {i + 1}" : L("Gpu"), g.Name),
            new(L("GpuMemory"), g.MemoryBytes is { } m ? ByteSize.Format(m, Culture) : "—"),
            new(L("Driver"), g.DriverVersion ?? "—"),
        }).Concat(s.Displays.Select((d, i) => new InfoRow(
            $"{L("Display")} {i + 1}{(d.IsPrimary ? " · " + L("Primary") : string.Empty)}",
            $"{d.Width} × {d.Height}{(d.RefreshHz is { } hz ? $" @ {hz} Hz" : string.Empty)}  ({d.Name})"))).ToList();
        groups.Add(new InfoGroup(L("Graphics"), "Image", graphics));
        return groups;
    }

    public override ReportSection? BuildReport()
    {
        if (_snapshot == null) return null;
        var section = new ReportSection(Title);
        foreach (var g in Groups)
        {
            var child = new ReportSection(g.Title);
            foreach (var r in g.Rows) child.Add(r.Label, r.Value);
            section.Children.Add(child);
        }
        return section;
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        (Title, _snapshot is { } s ? $"{s.ComputerName} · {s.WindowsEdition} {s.WindowsDisplayVersion} ({s.BuildNumber})" : null, DiagnosticOutcome.Info);
}

// ═══════════════════════════ Live performance ═══════════════════════════

public sealed partial class PerformanceToolViewModel : ToolViewModel
{
    private const int Capacity = 60;
    private readonly IPerformanceMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _cpu = new(), _mem = new(), _disk = new(), _rx = new(), _tx = new();
    private PerformanceSample? _last;
    private bool _sampling;

    public PerformanceToolViewModel(ToolContext context, IPerformanceMonitor monitor) : base(context)
    {
        _monitor = monitor;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public override string Key => "performance";
    public override string Icon => "Cpu";
    public override ToolCategory Category => ToolCategory.System;

    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _memorySeries = [];
    [ObservableProperty] private IReadOnlyList<double> _diskSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _receiveSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _sendSeries = [];
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _memoryDetail = string.Empty;
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private string _diskDetail = string.Empty;
    [ObservableProperty] private string _receiveText = "—";
    [ObservableProperty] private string _sendText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _processesText = "—";
    [ObservableProperty] private bool _diskAvailable = true;
    [ObservableProperty] private bool _isPaused;

    public override async Task OnShownAsync()
    {
        if (!IsPaused) _timer.Start();
        await TickAsync();
    }

    public override void OnHidden() => _timer.Stop();

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused) _timer.Stop();
        else _timer.Start();
        StatusText = IsPaused ? Loc["Perf_Paused"] : null;
    }

    private async Task TickAsync()
    {
        if (_sampling) return;
        if (System.Windows.Application.Current?.MainWindow?.WindowState == System.Windows.WindowState.Minimized) return;
        _sampling = true;
        try
        {
            var s = await Task.Run(_monitor.Sample);
            _last = s;
            Push(_cpu, s.CpuPercent);
            Push(_mem, s.MemoryTotal > 0 ? s.MemoryUsed * 100.0 / s.MemoryTotal : 0);
            Push(_disk, s.DiskActivePercent ?? 0);
            Push(_rx, s.NetworkReceiveBytesPerSecond);
            Push(_tx, s.NetworkSendBytesPerSecond);
            CpuSeries = [.. _cpu];
            MemorySeries = [.. _mem];
            DiskSeries = [.. _disk];
            ReceiveSeries = [.. _rx];
            SendSeries = [.. _tx];

            CpuText = Loc.Format("It_Percent", Math.Round(s.CpuPercent));
            MemoryText = Loc.Format("It_Percent", Math.Round(_mem.Last()));
            MemoryDetail = $"{ByteSize.Format(s.MemoryUsed, Culture)} / {ByteSize.Format(s.MemoryTotal, Culture)}";
            DiskAvailable = s.DiskActivePercent != null;
            DiskText = s.DiskActivePercent is { } d ? Loc.Format("It_Percent", Math.Round(d)) : Loc["It_Unavailable"];
            DiskDetail = s.DiskReadBytesPerSecond is { } r && s.DiskWriteBytesPerSecond is { } w
                ? Loc.Format("Perf_ReadWrite", ByteSize.FormatRate(r, Culture), ByteSize.FormatRate(w, Culture))
                : string.Empty;
            ReceiveText = ByteSize.FormatRate(s.NetworkReceiveBytesPerSecond, Culture);
            SendText = ByteSize.FormatRate(s.NetworkSendBytesPerSecond, Culture);
            UptimeText = ByteSize.FormatUptime(s.Uptime, Loc["Unit_DayShort"]);
            ProcessesText = s.ProcessCount.ToString(Culture);
            HasResult = true;
        }
        catch (Exception ex)
        {
            _timer.Stop();
            ShowError(ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > Capacity) q.Dequeue();
    }

    public override ReportSection? BuildReport()
    {
        if (_last is not { } s) return null;
        return new ReportSection(Title)
            .Add(Loc["Perf_Cpu"], CpuText)
            .Add(Loc["Perf_Memory"], $"{MemoryText} ({MemoryDetail})")
            .Add(Loc["Perf_Disk"], DiskAvailable ? $"{DiskText} {DiskDetail}".Trim() : Loc["It_Unavailable"])
            .Add(Loc["Perf_Download"], ReceiveText)
            .Add(Loc["Perf_Upload"], SendText)
            .Add(Loc["Perf_Processes"], ProcessesText)
            .Add(Loc["Sys_Uptime"], UptimeText)
            .Add(Loc["Perf_Average"], _cpu.Count > 0 ? Loc.Format("Perf_AverageValue", Math.Round(_cpu.Average()), _cpu.Count) : "—");
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        (Title, $"{Loc["Perf_Cpu"]} {CpuText} · {Loc["Perf_Memory"]} {MemoryText}", _last is { CpuPercent: > 90 } ? DiagnosticOutcome.Warning : DiagnosticOutcome.Info);
}

// ═══════════════════════════ Storage ═══════════════════════════

public sealed record DriveRow(string Name, string Label, string FileSystem, string Type, string Total, string Used, string Free, double UsedFraction, string Percent, string Tone, bool IsReady, bool IsSystem, string Path);

public sealed partial class StorageToolViewModel(ToolContext context, IStorageService storage) : ToolViewModel(context)
{
    public override string Key => "storage";
    public override string Icon => "Drive";
    public override ToolCategory Category => ToolCategory.Storage;

    [ObservableProperty] private IReadOnlyList<DriveRow> _drives = [];

    public override async Task OnShownAsync()
    {
        if (!IsBusy) await RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        var drives = await Task.Run(storage.GetDrives, ct);
        Drives = drives.Select(d => new DriveRow(
            d.Name.TrimEnd('\\'),
            d.Label ?? (d.IsReady ? Loc["Storage_NoLabel"] : Loc["Storage_NotReady"]),
            d.FileSystem ?? "—",
            Loc[$"DriveType_{d.DriveType}"],
            d.IsReady ? ByteSize.Format(d.TotalBytes, Culture) : "—",
            d.IsReady ? ByteSize.Format(d.UsedBytes, Culture) : "—",
            d.IsReady ? ByteSize.Format(d.FreeBytes, Culture) : "—",
            d.UsedFraction,
            d.IsReady ? Loc.Format("It_Percent", Math.Round(d.UsedFraction * 100)) : "—",
            !d.IsReady ? "Steel" : d.UsedFraction > 0.92 ? "Red" : d.UsedFraction > 0.8 ? "Amber" : "Green",
            d.IsReady,
            d.IsSystemDrive,
            d.Name)).ToList();
        HasResult = Drives.Count > 0;
        StatusText = Loc.Format("Storage_Count", Drives.Count);
    });

    [RelayCommand]
    private void OpenDrive(DriveRow? row)
    {
        if (row is { IsReady: true }) Context.Explorer.OpenFolder(row.Path);
    }

    [RelayCommand]
    private void CopyDrive(DriveRow? row)
    {
        if (row != null) Copy($"{row.Name} {row.Label} ({row.FileSystem}, {row.Type}) — {row.Used} / {row.Total} ({row.Percent}), {Loc["Storage_Free"]} {row.Free}", row.Name);
    }

    public override ReportSection? BuildReport()
    {
        if (Drives.Count == 0) return null;
        var table = new ReportTable(Loc["Storage_Drive"], Loc["Storage_Label"], Loc["Storage_FileSystem"], Loc["Storage_Type"], Loc["Storage_Total"], Loc["Storage_Used"], Loc["Storage_Free"], "%");
        foreach (var d in Drives) table.AddRow(d.Name, d.Label, d.FileSystem, d.Type, d.Total, d.Used, d.Free, d.Percent);
        return new ReportSection(Title) { Table = table };
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult()
    {
        var sys = Drives.FirstOrDefault(d => d.IsSystem);
        return (Title, sys == null ? StatusText : $"{sys.Name} {sys.Percent} · {Loc["Storage_Free"]} {sys.Free}",
            Drives.Any(d => d.Tone == "Red") ? DiagnosticOutcome.Warning : DiagnosticOutcome.Info);
    }
}

// ═══════════════════════════ Large file finder ═══════════════════════════

public sealed record LargeFileRow(string Name, string Folder, string FullPath, long SizeBytes, string Size, DateTime ModifiedLocal, string Modified);

public sealed partial class LargeFilesToolViewModel(ToolContext context, ILargeFileFinder finder) : ToolViewModel(context)
{
    private string? _scannedFolder;

    public override string Key => "largefiles";
    public override string Icon => "FileSearch";
    public override ToolCategory Category => ToolCategory.Storage;

    public IReadOnlyList<long> Presets { get; } = [100, 500, 1024];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand))] [NotifyPropertyChangedFor(nameof(FolderDisplay))] private string? _folder = context.Settings.Current.ItTools.LastLargeFileFolder;

    public string FolderDisplay => Folder ?? Loc["Large_NoFolder"];
    [ObservableProperty] private long _minimumMb = 500;
    [ObservableProperty] private string _customMb = "250";
    [ObservableProperty] private bool _useCustom;
    [ObservableProperty] private string? _inputError;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private IReadOnlyList<LargeFileRow> _files = [];
    [ObservableProperty] private LargeFileRow? _selected;

    [RelayCommand]
    private void ChooseFolder()
    {
        var picked = Context.Dialogs.PickFolder(Folder);
        if (picked == null) return;
        Folder = picked;
        Context.Settings.Current.ItTools.LastLargeFileFolder = picked;
        _ = Context.Settings.SaveAsync();
    }

    [RelayCommand]
    private void SetPreset(long mb)
    {
        UseCustom = false;
        MinimumMb = mb;
    }

    private bool CanScan() => !string.IsNullOrWhiteSpace(Folder);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync()
    {
        InputError = null;
        long mb = MinimumMb;
        if (UseCustom && !(long.TryParse(CustomMb.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out mb) && mb is >= 1 and <= 1_048_576))
        {
            InputError = Loc["Large_ErrSize"];
            return Task.CompletedTask;
        }
        var root = Folder!;
        return RunAsync(async ct =>
        {
            Files = [];
            HasResult = false;
            var progress = new Progress<LargeFileScanProgress>(p =>
                ProgressText = Loc.Format("Large_Progress", p.FilesScanned.ToString("N0", Culture), p.Found, p.CurrentFolder ?? string.Empty));
            var found = await finder.FindAsync(root, mb * 1024 * 1024, progress, ct);
            Files = found.Select(f =>
            {
                var local = DateTime.SpecifyKind(f.ModifiedUtc, DateTimeKind.Utc).ToLocalTime();
                return new LargeFileRow(f.Name, f.Folder, f.FullPath, f.SizeBytes, ByteSize.Format(f.SizeBytes, Culture), local, local.ToString("g", Culture));
            }).ToList();
            _scannedFolder = root;
            HasResult = true;
            StatusText = Loc.Format("Large_Done", Files.Count, ByteSize.Format(found.Sum(f => f.SizeBytes), Culture), ByteSize.Format(mb * 1024 * 1024, Culture));
            ProgressText = string.Empty;
        });
    }

    [RelayCommand]
    private void Reveal(LargeFileRow? row)
    {
        if ((row ?? Selected) is { } r) Context.Explorer.Reveal(r.FullPath);
    }

    [RelayCommand]
    private void CopyPath(LargeFileRow? row)
    {
        if ((row ?? Selected) is { } r) Copy(r.FullPath, r.Name);
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var table = new ReportTable(Loc["Large_Size"], Loc["Large_Modified"], Loc["Large_Path"]);
        foreach (var f in Files) table.AddRow(f.Size, f.Modified, f.FullPath);
        return new ReportSection(Title) { Table = table }.Add(Loc["Large_Folder"], _scannedFolder).Add(Loc["It_Result"], StatusText);
    }
}
