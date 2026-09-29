using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Arcadia.Core.ItTools.SystemInfo;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arcadia.ItTools;

/// <summary>
/// Inventory from WMI, the registry and Win32. Everything stays on this computer. Device serial numbers
/// are deliberately not collected.
/// </summary>
public sealed class SystemInformationService(ILogger<SystemInformationService> logger) : ISystemInformationService
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private string? _windowsName;
    private string? _cpuName;

    public Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    public SystemQuickStatus GetQuickStatus()
    {
        var (total, used) = Native.Memory();
        return new SystemQuickStatus(Environment.MachineName, _windowsName ??= WindowsShortName(), TimeSpan.FromMilliseconds(Environment.TickCount64), total, used, _cpuName ??= CpuNameFromRegistry());
    }

    private SystemSnapshot Collect(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var os = Query("SELECT Caption, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        ct.ThrowIfCancellationRequested();
        var cs = Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var bios = Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        ct.ThrowIfCancellationRequested();
        var cpus = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        var gpus = Query("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
        ct.ThrowIfCancellationRequested();

        using var cv = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        var build = cv?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        var ubr = cv?.GetValue("UBR") is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var displayVersion = cv?.GetValue("DisplayVersion") as string;

        var edition = Str(os, "Caption") ?? WindowsShortName();
        if (edition.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)) edition = edition["Microsoft ".Length..];

        var cpu = new CpuInfo(
            cpus.Select(c => Str(c, "Name")?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? CpuNameFromRegistry(),
            cpus.Sum(c => Int(c, "NumberOfCores") ?? 0) is > 0 and var cores ? cores : Environment.ProcessorCount,
            cpus.Sum(c => Int(c, "NumberOfLogicalProcessors") ?? 0) is > 0 and var logical ? logical : Environment.ProcessorCount,
            cpus.Select(c => Int(c, "MaxClockSpeed")).FirstOrDefault());

        long installed = Native.GetPhysicallyInstalledSystemMemory(out var kb) ? (long)kb * 1024 : Long(cs, "TotalPhysicalMemory") ?? 0;

        var snapshot = new SystemSnapshot(
            Environment.MachineName,
            $"{Environment.UserDomainName}\\{Environment.UserName}",
            edition,
            displayVersion,
            build + ubr,
            RuntimeInformation.OSArchitecture.ToString(),
            Str(cs, "Manufacturer"),
            Str(cs, "Model"),
            new FirmwareInfo(FirmwareType(), Str(bios, "Manufacturer"), Str(bios, "SMBIOSBIOSVersion"), Date(bios, "ReleaseDate"), SecureBoot()),
            cpu,
            installed,
            gpus.Select(g => ToGpu(g)).Where(g => g.Name.Length > 0).ToList(),
            Displays(),
            Date(os, "InstallDate")?.ToUniversalTime(),
            Date(os, "LastBootUpTime")?.ToUniversalTime(),
            TimeSpan.FromMilliseconds(Environment.TickCount64));

        foreach (var m in new[] { os, cs, bios }.Concat(cpus).Concat(gpus)) m?.Dispose();
        logger.LogInformation("System inventory collected in {Ms:0} ms", clock.Elapsed.TotalMilliseconds);
        return snapshot;
    }

    private List<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "WMI query failed: {Query}", wql);
            return [];
        }
    }

    private static string? Str(ManagementBaseObject? o, string name) => o?[name]?.ToString()?.Trim() is { Length: > 0 } s ? s : null;
    private static int? Int(ManagementBaseObject? o, string name) => o?[name] is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
    private static long? Long(ManagementBaseObject? o, string name) => o?[name] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;

    private static DateTime? Date(ManagementBaseObject? o, string name)
    {
        if (Str(o, name) is not { } text) return null;
        try { return ManagementDateTimeConverter.ToDateTime(text); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static GpuInfo ToGpu(ManagementBaseObject g)
    {
        var name = Str(g, "Name") ?? string.Empty;
        // AdapterRAM is a 32-bit value (caps at 4 GB); the driver's registry entry has the real size.
        long? memory = GpuMemoryFromRegistry(name) ?? (Long(g, "AdapterRAM") is > 0 and var ram ? ram : null);
        return new GpuInfo(name, memory, Str(g, "DriverVersion"));
    }

    private static long? GpuMemoryFromRegistry(string name)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var key = cls.OpenSubKey(sub);
                if (!string.Equals(key?.GetValue("DriverDesc") as string, name, StringComparison.OrdinalIgnoreCase)) continue;
                return key?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l when l > 0 => l,
                    byte[] { Length: 8 } b => BitConverter.ToInt64(b),
                    _ => null,
                };
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    private static IReadOnlyList<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        var adapter = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
        for (int i = 0; Native.EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter.Size = Marshal.SizeOf<Native.DisplayDevice>())
        {
            if ((adapter.StateFlags & Native.DisplayAttachedToDesktop) == 0) continue;
            var mode = new Native.DevMode { Size = (short)Marshal.SizeOf<Native.DevMode>() };
            if (!Native.EnumDisplaySettings(adapter.DeviceName, Native.EnumCurrentSettings, ref mode)) continue;
            var monitor = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
            var monitorName = Native.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceString : adapter.DeviceString;
            list.Add(new DisplayInfo(monitorName, mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency > 1 ? mode.DisplayFrequency : null,
                (adapter.StateFlags & Native.DisplayPrimaryDevice) != 0));
        }
        return list.OrderByDescending(d => d.IsPrimary).ToList();
    }

    private static string FirmwareType() =>
        Native.GetFirmwareType(out var t) ? t switch { 1 => "BIOS", 2 => "UEFI", _ => "Unknown" } : "Unknown";

    private static bool? SecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"Windows 11 Pro 24H2" — ProductName in the registry still says "Windows 10" on Windows 11.</summary>
    internal static string WindowsShortName()
    {
        using var cv = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        var product = cv?.GetValue("ProductName") as string ?? "Windows";
        var build = int.TryParse(cv?.GetValue("CurrentBuild") as string, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : Environment.OSVersion.Version.Build;
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        return cv?.GetValue("DisplayVersion") is string dv ? $"{product} {dv}" : product;
    }

    private static string CpuNameFromRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? RuntimeInformation.ProcessArchitecture.ToString();
    }
}

/// <summary>
/// Samples CPU (GetSystemTimes), memory, disk (performance counters, created lazily and optional) and network
/// throughput. Each sample costs well under a millisecond after the first.
/// </summary>
public sealed class PerformanceMonitor(ILogger<PerformanceMonitor> logger) : IPerformanceMonitor, IDisposable
{
    private readonly Lock _gate = new();
    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private long _lastTicks;
    private System.Diagnostics.PerformanceCounter? _diskRead, _diskWrite, _diskIdle;
    private Task? _diskInit;

    public PerformanceSample Sample()
    {
        lock (_gate)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            double seconds = _lastTicks == 0 ? 0 : (nowTicks - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = nowTicks;

            double cpu = 0;
            if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long dIdle = idle - _idle, dTotal = kernel - _kernel + (user - _user);
                if (_kernel != 0 && dTotal > 0) cpu = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
                (_idle, _kernel, _user) = (idle, kernel, user);
            }

            var (total, used) = Native.Memory();
            var (rx, tx) = NetworkBytes();
            double rxRate = seconds > 0 && _rx > 0 ? Math.Max(0, (rx - _rx) / seconds) : 0;
            double txRate = seconds > 0 && _tx > 0 ? Math.Max(0, (tx - _tx) / seconds) : 0;
            (_rx, _tx) = (rx, tx);

            // Performance counters can take seconds to open the first time; never make a sample wait for them.
            _diskInit ??= Task.Run(CreateDiskCounters);
            bool diskReady = _diskInit.IsCompletedSuccessfully;
            double? read = diskReady ? Next(_diskRead) : null, write = diskReady ? Next(_diskWrite) : null, idlePct = diskReady ? Next(_diskIdle) : null;

            var info = new Native.PerformanceInformation { Size = (uint)Marshal.SizeOf<Native.PerformanceInformation>() };
            int processes = Native.GetPerformanceInfo(ref info, info.Size) ? (int)info.ProcessCount : 0;

            return new PerformanceSample(DateTime.UtcNow, cpu, used, total,
                idlePct is { } ip ? Math.Clamp(100 - ip, 0, 100) : null, read, write, rxRate, txRate,
                TimeSpan.FromMilliseconds(Environment.TickCount64), processes);
        }
    }

    private static (long Rx, long Tx) NetworkBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return (rx, tx);
    }

    private void CreateDiskCounters()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var read = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            var write = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            var idle = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            read.NextValue(); write.NextValue(); idle.NextValue();
            lock (_gate) (_diskRead, _diskWrite, _diskIdle) = (read, write, idle);
            logger.LogInformation("Disk performance counters ready in {Ms:0} ms", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "Disk performance counters are unavailable; disk activity will not be shown");
        }
    }

    private static double? Next(System.Diagnostics.PerformanceCounter? counter)
    {
        if (counter == null) return null;
        try { return counter.NextValue(); }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        _diskIdle?.Dispose();
    }
}
