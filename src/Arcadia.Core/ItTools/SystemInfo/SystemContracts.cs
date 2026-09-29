namespace Arcadia.Core.ItTools.SystemInfo;

public sealed record FirmwareInfo(string Type, string? Vendor, string? Version, DateTime? ReleaseDate, bool? SecureBoot);

public sealed record CpuInfo(string Name, int PhysicalCores, int LogicalProcessors, int? MaxClockMhz);

public sealed record GpuInfo(string Name, long? MemoryBytes, string? DriverVersion);

public sealed record DisplayInfo(string Name, int Width, int Height, int? RefreshHz, bool IsPrimary);

/// <summary>A point-in-time description of this computer. Never leaves the machine.</summary>
public sealed record SystemSnapshot(
    string ComputerName,
    string UserName,
    string WindowsEdition,
    string? WindowsDisplayVersion,
    string BuildNumber,
    string Architecture,
    string? Manufacturer,
    string? Model,
    FirmwareInfo Firmware,
    CpuInfo Cpu,
    long InstalledMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DisplayInfo> Displays,
    DateTime? InstalledAtUtc,
    DateTime? LastBootUtc,
    TimeSpan Uptime);

/// <summary>Values that are cheap to read often (status strip).</summary>
public sealed record SystemQuickStatus(string ComputerName, string WindowsName, TimeSpan Uptime, long MemoryTotal, long MemoryUsed, string CpuName)
{
    public double MemoryUsedFraction => MemoryTotal <= 0 ? 0 : (double)MemoryUsed / MemoryTotal;
}

public interface ISystemInformationService
{
    /// <summary>Full inventory (WMI and registry; can take a second).</summary>
    Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    SystemQuickStatus GetQuickStatus();
}

// ─────────────────────────── live performance ───────────────────────────

public sealed record PerformanceSample(
    DateTime TakenAtUtc,
    double CpuPercent,
    long MemoryUsed,
    long MemoryTotal,
    double? DiskActivePercent,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double NetworkReceiveBytesPerSecond,
    double NetworkSendBytesPerSecond,
    TimeSpan Uptime,
    int ProcessCount);

/// <summary>Stateful sampler: rates are computed from the previous call, so call it at a steady interval.</summary>
public interface IPerformanceMonitor
{
    PerformanceSample Sample();
}

// ─────────────────────────── processes ───────────────────────────

public sealed record ProcessEntry(int Id, string Name, double? CpuPercent, long WorkingSetBytes, long PrivateBytes, DateTime? StartedAtUtc, string? Path, int SessionId, int Threads)
{
    public bool IsCritical => ProcessSafety.IsCritical(Name, Id, SessionId);
}

public enum ProcessTerminateOutcome { Terminated, AccessDenied, NotFound, Failed }

public interface IProcessService
{
    /// <summary>All processes. CPU usage is measured since the previous call (null on the first one).</summary>
    Task<IReadOnlyList<ProcessEntry>> GetProcessesAsync(CancellationToken cancellationToken = default);

    Task<ProcessTerminateOutcome> TerminateAsync(int processId, CancellationToken cancellationToken = default);

    /// <summary>Asks Windows (UAC) to end the process with administrator rights. False if the user declined or it failed.</summary>
    Task<bool> TerminateElevatedAsync(int processId, CancellationToken cancellationToken = default);
}

/// <summary>Processes whose termination would crash or destabilise Windows.</summary>
public static class ProcessSafety
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "Memory Compression", "Secure System", "MsMpEng", "sihost", "LsaIso",
    };

    public static bool IsCritical(string name, int id, int sessionId) =>
        id is 0 or 4 || CriticalNames.Contains(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
}

// ─────────────────────────── services ───────────────────────────

public sealed record ServiceEntry(string Name, string DisplayName, string Status, bool IsRunning, bool IsStopped, string StartType, bool CanStop);

public enum ServiceAction { Start, Stop, Restart }

public enum ServiceControlOutcome { Done, RequiresElevation, Declined, Failed }

public sealed record ServiceControlResult(ServiceControlOutcome Outcome, string? Detail = null);

public interface IWindowsServiceManager
{
    Task<IReadOnlyList<ServiceEntry>> GetServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Tries with the current rights; reports <see cref="ServiceControlOutcome.RequiresElevation"/> when Windows refuses.</summary>
    Task<ServiceControlResult> ControlAsync(string serviceName, ServiceAction action, CancellationToken cancellationToken = default);

    /// <summary>Runs the same action through a UAC prompt (only after the user agreed).</summary>
    Task<ServiceControlResult> ControlElevatedAsync(string serviceName, ServiceAction action, CancellationToken cancellationToken = default);
}

// ─────────────────────────── event log ───────────────────────────

public enum EventSeverity { Critical = 1, Error = 2, Warning = 3, Information = 4 }

public sealed record EventEntry(DateTime TimeUtc, EventSeverity Level, string Source, int EventId, string Message, long? RecordId);

public sealed record EventLogQuery(string LogName, IReadOnlySet<EventSeverity> Levels, int MaxEvents, string? Search);

public interface IEventLogService
{
    /// <summary>Newest events first. Access to the Security log normally requires administrator rights.</summary>
    Task<IReadOnlyList<EventEntry>> QueryAsync(EventLogQuery query, CancellationToken cancellationToken = default);
}
