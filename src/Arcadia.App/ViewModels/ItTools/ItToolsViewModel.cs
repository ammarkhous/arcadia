using System.Collections.ObjectModel;
using System.Windows.Threading;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Network;
using Arcadia.Core.ItTools.SystemInfo;
using Arcadia.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.ItTools;

/// <summary>A tool in the rail / overview. The tool's view model is created the first time it is opened.</summary>
public sealed partial class ToolEntry(string key, string icon, ToolCategory category, Type viewModelType) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public ToolCategory Category { get; } = category;
    public Type ViewModelType { get; } = viewModelType;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ToolCategoryGroup(ToolCategory category, string icon, IReadOnlyList<ToolEntry> tools) : ObservableObject
{
    public ToolCategory Category { get; } = category;
    public string Icon { get; } = icon;
    public IReadOnlyList<ToolEntry> Tools { get; } = tools;

    [ObservableProperty] private string _title = string.Empty;
}

/// <summary>One cell of the status strip at the top of IT Tools.</summary>
public sealed partial class StatusCell(string key, string icon) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _value = "…";
    [ObservableProperty] private string? _detail;
    /// <summary>Lamp colour, or null for no lamp.</summary>
    [ObservableProperty] private string? _tone;
    /// <summary>0..1 fill for a small meter, or null.</summary>
    [ObservableProperty] private double? _meter;
}

/// <summary>
/// The IT Tools module: a status strip, a categorised tool rail and the current tool (or the overview).
/// Live readings refresh every 2 seconds only while the page is visible and the window is not minimised.
/// </summary>
public sealed partial class ItToolsViewModel : ObservableObject, IActivatable, IDeactivatable
{
    private readonly IServiceProvider _services;
    private readonly ILocalizer _loc;
    private readonly ISystemInformationService _system;
    private readonly INetworkInformationService _network;
    private readonly IStorageService _storage;
    private readonly IDiagnosticSessionStore _sessionStore;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ItToolsViewModel> _logger;
    private readonly Dictionary<string, ToolViewModel> _tools = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;
    private int _ticks;

    public ItToolsViewModel(IServiceProvider services, ILocalizer loc, ISystemInformationService system, INetworkInformationService network,
        IStorageService storage, IDiagnosticSessionStore sessionStore, DiagnosticSessionContext sessions, IDialogService dialogs, ILogger<ItToolsViewModel> logger)
    {
        _services = services;
        _loc = loc;
        _system = system;
        _network = network;
        _storage = storage;
        _sessionStore = sessionStore;
        _dialogs = dialogs;
        _logger = logger;
        Sessions = sessions;

        Entries =
        [
            new("sysinfo", "Monitor", ToolCategory.System, typeof(SystemInfoToolViewModel)),
            new("performance", "Cpu", ToolCategory.System, typeof(PerformanceToolViewModel)),
            new("network", "Network", ToolCategory.Network, typeof(NetworkAdaptersToolViewModel)),
            new("ping", "Ping", ToolCategory.Network, typeof(PingToolViewModel)),
            new("trace", "Route", ToolCategory.Network, typeof(TracerouteToolViewModel)),
            new("dns", "Dns", ToolCategory.Network, typeof(DnsToolViewModel)),
            new("port", "Port", ToolCategory.Network, typeof(PortTestToolViewModel)),
            new("devices", "Devices", ToolCategory.Network, typeof(LocalDevicesToolViewModel)),
            new("storage", "Drive", ToolCategory.Storage, typeof(StorageToolViewModel)),
            new("largefiles", "FileSearch", ToolCategory.Storage, typeof(LargeFilesToolViewModel)),
            new("processes", "Processes", ToolCategory.Processes, typeof(ProcessesToolViewModel)),
            new("services", "Services", ToolCategory.Windows, typeof(ServicesToolViewModel)),
            new("eventlog", "EventLog", ToolCategory.Windows, typeof(EventLogToolViewModel)),
            new("commands", "Terminal", ToolCategory.Windows, typeof(QuickCommandsToolViewModel)),
            new("session", "Diagnostic", ToolCategory.Diagnostics, typeof(DiagnosticSessionToolViewModel)),
            new("hash", "Hash", ToolCategory.Utilities, typeof(HashToolViewModel)),
            new("subnet", "Subnet", ToolCategory.Utilities, typeof(SubnetToolViewModel)),
        ];
        var icons = new Dictionary<ToolCategory, string>
        {
            [ToolCategory.System] = "Monitor", [ToolCategory.Network] = "Network", [ToolCategory.Storage] = "Drive",
            [ToolCategory.Processes] = "Processes", [ToolCategory.Windows] = "WindowsLogo", [ToolCategory.Diagnostics] = "Diagnostic",
            [ToolCategory.Utilities] = "ITTools",
        };
        Groups = Entries.GroupBy(e => e.Category).Select(g => new ToolCategoryGroup(g.Key, icons[g.Key], g.ToList())).ToList();
        ToolCategoryGroup G(ToolCategory c) => Groups.First(g => g.Category == c);
        // Balanced by height for the overview "control panel".
        OverviewColumns =
        [
            [G(ToolCategory.System), G(ToolCategory.Storage), G(ToolCategory.Processes)],
            [G(ToolCategory.Network)],
            [G(ToolCategory.Windows), G(ToolCategory.Utilities), G(ToolCategory.Diagnostics)],
        ];

        Status =
        [
            new("computer", "Monitor"), new("windows", "WindowsLogo"), new("uptime", "Clock"), new("cpu", "Cpu"),
            new("memory", "Memory"), new("disk", "Drive"), new("network", "Network"), new("ip", "Port"),
        ];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshStatusAsync(full: false);

        RefreshLabels();
        loc.CultureChanged += (_, _) => { RefreshLabels(); _ = RefreshStatusAsync(full: true); };
        sessions.Changed += async (_, _) => await LoadRecentSessionsAsync();
    }

    public IReadOnlyList<ToolEntry> Entries { get; }
    public IReadOnlyList<ToolCategoryGroup> Groups { get; }
    public IReadOnlyList<IReadOnlyList<ToolCategoryGroup>> OverviewColumns { get; }
    public IReadOnlyList<StatusCell> Status { get; }
    public DiagnosticSessionContext Sessions { get; }
    public ObservableCollection<DiagnosticSessionSummary> RecentSessions { get; } = [];

    [ObservableProperty] private ToolViewModel? _currentTool;
    [ObservableProperty] private bool _isOverview = true;
    [ObservableProperty] private string _subtitle = string.Empty;

    private bool _active;
    private SystemQuickStatus? _lastQuick;

    public async Task ActivateAsync()
    {
        _active = true;
        await RefreshStatusAsync(full: true);
        _timer.Start();
        await Sessions.EnsureLoadedAsync();
        await LoadRecentSessionsAsync();
        if (CurrentTool != null) await CurrentTool.OnShownAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _timer.Stop();
        CurrentTool?.OnHidden();
    }

    [RelayCommand]
    public async Task OpenToolAsync(string? key)
    {
        var entry = Entries.FirstOrDefault(e => e.Key == key);
        if (entry == null) { ShowOverview(); return; }
        if (!_tools.TryGetValue(entry.Key, out var tool))
        {
            tool = (ToolViewModel)ActivatorUtilities.CreateInstance(_services, entry.ViewModelType);
            _tools[entry.Key] = tool;
        }
        if (ReferenceEquals(tool, CurrentTool)) return;
        CurrentTool?.OnHidden();
        foreach (var e in Entries) e.IsSelected = e == entry;
        CurrentTool = tool;
        IsOverview = false;
        _logger.LogDebug("IT tool {Tool} opened", entry.Key);
        try
        {
            if (_active) await tool.OnShownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening IT tool {Tool} failed", entry.Key);
        }
    }

    [RelayCommand]
    public void ShowOverview()
    {
        CurrentTool?.OnHidden();
        CurrentTool = null;
        IsOverview = true;
        foreach (var e in Entries) e.IsSelected = false;
        _ = LoadRecentSessionsAsync();
    }

    [RelayCommand]
    private async Task StartSessionAsync()
    {
        var editor = new DiagnosticSessionEditorViewModel(_loc, null, null);
        if (!_dialogs.Show(editor)) return;
        await Sessions.StartAsync(editor.SessionTitle.Trim(), editor.Reference);
        await OpenToolAsync("session");
    }

    [RelayCommand]
    private Task OpenSessionsAsync() => OpenToolAsync("session");

    [RelayCommand]
    private async Task OpenSessionAsync(DiagnosticSessionSummary? summary)
    {
        await OpenToolAsync("session");
        if (summary != null && CurrentTool is DiagnosticSessionToolViewModel sessionTool) await sessionTool.SelectAsync(summary.Id);
    }

    /// <summary>Opens one diagnostic session (links from tasks, notes and events).</summary>
    public async Task ShowSessionAsync(int id)
    {
        await OpenToolAsync("session");
        if (CurrentTool is DiagnosticSessionToolViewModel sessionTool) await sessionTool.SelectAsync(id);
    }

    private async Task LoadRecentSessionsAsync()
    {
        try
        {
            var list = await _sessionStore.ListAsync();
            RecentSessions.Clear();
            foreach (var s in list.Take(6)) RecentSessions.Add(s);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recent diagnostic sessions could not be loaded");
        }
    }

    /// <summary>
    /// Cheap values (uptime, memory, CPU load) every tick; network every other tick; disk usage every 15th.
    /// Nothing runs while the window is minimised.
    /// </summary>
    private async Task RefreshStatusAsync(bool full)
    {
        if (!_active) return;
        if (System.Windows.Application.Current?.MainWindow?.WindowState == System.Windows.WindowState.Minimized && !full) return;
        _ticks++;
        try
        {
            var monitor = _services.GetRequiredService<IPerformanceMonitor>();
            var (quick, sample, net, drives) = await Task.Run(() =>
            {
                var q = _system.GetQuickStatus();
                var s = monitor.Sample();
                var n = full || _ticks % 2 == 0 ? _network.GetQuickStatus() : null;
                var d = full || _ticks % 15 == 0 ? _storage.GetDrives() : null;
                return (q, s, n, d);
            });
            _lastQuick = quick;
            Cell("computer").Value = quick.ComputerName;
            Cell("windows").Value = quick.WindowsName;
            Cell("uptime").Value = ByteSize.FormatUptime(quick.Uptime, _loc["Unit_DayShort"]);
            var cpu = Cell("cpu");
            cpu.Value = _loc.Format("It_Percent", Math.Round(sample.CpuPercent));
            cpu.Detail = quick.CpuName;
            cpu.Meter = sample.CpuPercent / 100;
            cpu.Tone = sample.CpuPercent > 90 ? "Red" : sample.CpuPercent > 70 ? "Amber" : "Green";
            var mem = Cell("memory");
            mem.Value = $"{ByteSize.Format(quick.MemoryUsed, _loc.Culture)} / {ByteSize.Format(quick.MemoryTotal, _loc.Culture)}";
            mem.Meter = quick.MemoryUsedFraction;
            mem.Tone = quick.MemoryUsedFraction > 0.9 ? "Red" : quick.MemoryUsedFraction > 0.75 ? "Amber" : "Green";
            if (net != null)
            {
                var n = Cell("network");
                n.Value = net.IsConnected ? net.AdapterName ?? _loc["It_Connected"] : _loc["It_Offline"];
                n.Tone = net.IsConnected ? "Green" : "Red";
                var ip = Cell("ip");
                ip.Value = net.LocalIPv4 ?? "—";
                ip.Detail = net.Prefix is { } p ? "/" + p : null;
            }
            if (drives != null && drives.FirstOrDefault(d => d.IsSystemDrive) is { } sys)
            {
                var disk = Cell("disk");
                disk.Value = _loc.Format("It_DiskFree", ByteSize.Format(sys.FreeBytes, _loc.Culture), sys.Name.TrimEnd('\\'));
                disk.Meter = sys.UsedFraction;
                disk.Tone = sys.UsedFraction > 0.92 ? "Red" : sys.UsedFraction > 0.8 ? "Amber" : "Green";
            }
            Subtitle = _loc.Format("It_Subtitle", quick.ComputerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IT Tools status strip refresh failed");
        }
    }

    private StatusCell Cell(string key) => Status.First(s => s.Key == key);

    private void RefreshLabels()
    {
        foreach (var e in Entries)
        {
            e.Title = _loc[$"Tool_{e.Key}"];
            e.Description = _loc[$"Tool_{e.Key}_Desc"];
        }
        foreach (var g in Groups) g.Title = _loc[$"ItCat_{g.Category}"];
        foreach (var s in Status) s.Label = _loc[$"ItStatus_{s.Key}"];
        Subtitle = _loc.Format("It_Subtitle", _lastQuick?.ComputerName ?? Environment.MachineName);
    }
}
