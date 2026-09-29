using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.SystemInfo;
using Microsoft.Extensions.Logging;

namespace Arcadia.ItTools;

public sealed class ProcessService(ILogger<ProcessService> logger) : IProcessService
{
    private const int ErrorCancelled = 1223; // the user declined the UAC prompt
    private readonly Lock _gate = new();
    private Dictionary<(int Pid, long Created), long> _lastCpu = new();
    private long _lastSampleTicks;

    public Task<IReadOnlyList<ProcessEntry>> GetProcessesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ProcessEntry>>(() =>
        {
            var now = Stopwatch.GetTimestamp();
            var processes = Process.GetProcesses();
            var list = new List<ProcessEntry>(processes.Length);
            var cpu = new Dictionary<(int, long), long>(processes.Length);
            double elapsed100ns;
            Dictionary<(int, long), long> previous;
            lock (_gate)
            {
                elapsed100ns = _lastSampleTicks == 0 ? 0 : (now - _lastSampleTicks) * 10_000_000.0 / Stopwatch.Frequency;
                previous = _lastCpu;
            }

            foreach (var p in processes)
            {
                using (p)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long created = 0, busy = -1;
                    string? path = null;
                    var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, p.Id);
                    if (handle != 0)
                    {
                        try
                        {
                            if (Native.GetProcessTimes(handle, out created, out _, out var kernel, out var user)) busy = kernel + user;
                            var buffer = new char[1024];
                            int size = buffer.Length;
                            if (Native.QueryFullProcessImageName(handle, 0, buffer, ref size)) path = new string(buffer, 0, size);
                        }
                        finally
                        {
                            Native.CloseHandle(handle);
                        }
                    }

                    double? cpuPercent = null;
                    if (busy >= 0)
                    {
                        cpu[(p.Id, created)] = busy;
                        if (elapsed100ns > 0 && previous.TryGetValue((p.Id, created), out var before))
                            cpuPercent = Math.Clamp((busy - before) * 100.0 / (elapsed100ns * Environment.ProcessorCount), 0, 100);
                    }

                    int threads = 0;
                    long ws = 0, priv = 0;
                    try
                    {
                        threads = p.Threads.Count;
                        ws = p.WorkingSet64;
                        priv = p.PrivateMemorySize64;
                    }
                    catch (InvalidOperationException) { continue; } // exited meanwhile

                    list.Add(new ProcessEntry(p.Id, p.Id == 0 ? "Idle" : p.ProcessName, cpuPercent, ws, priv, Native.FileTimeToUtc(created), path, p.SessionId, threads));
                }
            }

            lock (_gate)
            {
                _lastCpu = cpu;
                _lastSampleTicks = now;
            }
            return list;
        }, cancellationToken);

    public Task<ProcessTerminateOutcome> TerminateAsync(int processId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                using var p = Process.GetProcessById(processId);
                logger.LogWarning("User requested termination of process {Name} ({Pid})", p.ProcessName, processId);
                p.Kill();
                p.WaitForExit(5000);
                return ProcessTerminateOutcome.Terminated;
            }
            catch (ArgumentException) { return ProcessTerminateOutcome.NotFound; }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                return ProcessTerminateOutcome.AccessDenied;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                logger.LogWarning(ex, "Terminating process {Pid} failed", processId);
                return ex is Win32Exception { NativeErrorCode: 5 } ? ProcessTerminateOutcome.AccessDenied : ProcessTerminateOutcome.Failed;
            }
        }, cancellationToken);

    public Task<bool> TerminateElevatedAsync(int processId, CancellationToken cancellationToken = default) =>
        ElevatedRunner.RunAsync("taskkill.exe", $"/PID {processId} /F", logger, cancellationToken);
}

/// <summary>Runs one fixed system command through a UAC prompt. Arguments are built by Arcadia, never from free text.</summary>
internal static class ElevatedRunner
{
    public static Task<bool> RunAsync(string fileName, string arguments, ILogger logger, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p == null) return false;
                p.WaitForExit(60_000);
                logger.LogInformation("Elevated {File} {Args} exited with {Code}", fileName, arguments, p.HasExited ? p.ExitCode : -1);
                return p.HasExited && p.ExitCode == 0;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                logger.LogInformation("User declined elevation for {File}", fileName);
                return false;
            }
            catch (Win32Exception ex)
            {
                logger.LogWarning(ex, "Elevated {File} could not be started", fileName);
                return false;
            }
        }, ct);
}

public sealed partial class WindowsServiceManager(ILogger<WindowsServiceManager> logger) : IWindowsServiceManager
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public Task<IReadOnlyList<ServiceEntry>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ServiceEntry>>(() =>
        {
            var services = ServiceController.GetServices();
            try
            {
                return services.Select(ToEntry).OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            finally
            {
                foreach (var s in services) s.Dispose();
            }
        }, cancellationToken);

    private static ServiceEntry ToEntry(ServiceController s)
    {
        string start;
        try { start = s.StartType.ToString(); }
        catch (InvalidOperationException) { start = "Unknown"; }
        var status = s.Status;
        return new ServiceEntry(s.ServiceName, s.DisplayName, status.ToString(), status == ServiceControllerStatus.Running, status == ServiceControllerStatus.Stopped, start, SafeCanStop(s));
    }

    private static bool SafeCanStop(ServiceController s)
    {
        try { return s.CanStop; }
        catch (InvalidOperationException) { return false; }
    }

    public Task<ServiceControlResult> ControlAsync(string serviceName, ServiceAction action, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var sc = new ServiceController(serviceName);
            try
            {
                logger.LogInformation("Service {Action} requested for {Service}", action, serviceName);
                if (action is ServiceAction.Stop or ServiceAction.Restart && sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, WaitTimeout);
                }
                if (action is ServiceAction.Start or ServiceAction.Restart)
                {
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, WaitTimeout);
                    }
                }
                return new ServiceControlResult(ServiceControlOutcome.Done);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 5 })
            {
                return new ServiceControlResult(ServiceControlOutcome.RequiresElevation);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or Win32Exception)
            {
                logger.LogWarning(ex, "Service {Action} failed for {Service}", action, serviceName);
                return new ServiceControlResult(ServiceControlOutcome.Failed, ex.InnerException?.Message ?? ex.Message);
            }
        }, cancellationToken);

    public async Task<ServiceControlResult> ControlElevatedAsync(string serviceName, ServiceAction action, CancellationToken cancellationToken = default)
    {
        // Only real service names reach the command line.
        if (!SafeServiceName().IsMatch(serviceName)) return new ServiceControlResult(ServiceControlOutcome.Failed, "Invalid service name.");
        var known = await GetServicesAsync(cancellationToken).ConfigureAwait(false);
        if (!known.Any(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase)))
            return new ServiceControlResult(ServiceControlOutcome.Failed, "Unknown service.");

        var verb = action switch { ServiceAction.Start => "Start-Service", ServiceAction.Stop => "Stop-Service", _ => "Restart-Service" };
        var quoted = serviceName.Replace("'", "''", StringComparison.Ordinal);
        var script = $"try {{ {verb} -Name '{quoted}' -Force -ErrorAction Stop; exit 0 }} catch {{ exit 1 }}";
        var ok = await ElevatedRunner.RunAsync("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"", logger, cancellationToken).ConfigureAwait(false);
        return ok ? new ServiceControlResult(ServiceControlOutcome.Done) : new ServiceControlResult(ServiceControlOutcome.Declined);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-$@{} ]{1,256}$")]
    private static partial Regex SafeServiceName();
}

public sealed class EventLogService(ILogger<EventLogService> logger) : IEventLogService
{
    private const int ScanLimitFactor = 40;

    public Task<IReadOnlyList<EventEntry>> QueryAsync(Arcadia.Core.ItTools.SystemInfo.EventLogQuery query, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<EventEntry>>(() =>
        {
            var xpath = BuildXPath(query.Levels);
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery(query.LogName, PathType.LogName, xpath) { ReverseDirection = true };
            var result = new List<EventEntry>(Math.Min(query.MaxEvents, 1000));
            try
            {
                using var reader = new EventLogReader(q);
                int scanned = 0, scanLimit = query.MaxEvents * ScanLimitFactor;
                while (result.Count < query.MaxEvents && scanned++ < scanLimit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var record = reader.ReadEvent();
                    if (record == null) break;
                    var entry = ToEntry(record);
                    if (!string.IsNullOrWhiteSpace(query.Search) && !Matches(entry, query.Search)) continue;
                    result.Add(entry);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ToolException(ToolFailure.AccessDenied, $"Reading the {query.LogName} log requires administrator rights.", ex);
            }
            catch (EventLogNotFoundException ex)
            {
                throw new ToolException(ToolFailure.NotFound, $"The {query.LogName} log does not exist.", ex);
            }
            catch (EventLogException ex)
            {
                logger.LogWarning(ex, "Event log query failed for {Log}", query.LogName);
                throw new ToolException(ToolFailure.Unavailable, ex.Message, ex);
            }
            return result;
        }, cancellationToken);

    internal static string BuildXPath(IReadOnlySet<EventSeverity> levels)
    {
        if (levels.Count == 0 || levels.Count == 4) return "*";
        var parts = new List<string>();
        foreach (var l in levels.OrderBy(l => l))
        {
            parts.Add($"Level={(int)l}");
            if (l == EventSeverity.Information) { parts.Add("Level=0"); parts.Add("Level=5"); }
        }
        return $"*[System[({string.Join(" or ", parts)})]]";
    }

    private static bool Matches(EventEntry e, string search) =>
        e.Message.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || e.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || e.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture) == search.Trim();

    private static EventEntry ToEntry(EventRecord r)
    {
        string message;
        try
        {
            message = r.FormatDescription() ?? string.Join(" | ", r.Properties.Select(p => p.Value?.ToString()));
        }
        catch (EventLogException)
        {
            message = string.Join(" | ", r.Properties.Select(p => p.Value?.ToString()));
        }
        var level = r.Level switch
        {
            1 => EventSeverity.Critical,
            2 => EventSeverity.Error,
            3 => EventSeverity.Warning,
            _ => EventSeverity.Information,
        };
        return new EventEntry(r.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue, level, r.ProviderName ?? string.Empty, r.Id, message.Trim(), r.RecordId);
    }
}

public sealed class QuickCommandLauncher(ILogger<QuickCommandLauncher> logger) : IQuickCommandLauncher
{
    public bool Launch(QuickCommand command)
    {
        // Only catalogued commands can be started.
        if (QuickCommands.Find(command.Key) != command) return false;
        try
        {
            var info = new ProcessStartInfo(command.FileName, command.Arguments ?? string.Empty) { UseShellExecute = true };
            if (command.RunAsAdministrator) info.Verb = "runas";
            Process.Start(info)?.Dispose();
            logger.LogInformation("Quick command {Key} started", command.Key);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // UAC declined
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Quick command {Key} ({File}) could not be started", command.Key, command.FileName);
            return false;
        }
    }
}
