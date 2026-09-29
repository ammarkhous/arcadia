using System.Globalization;
using Arcadia.Core.ItTools.Network;

namespace Arcadia.Core.Oracle.Commands;

/// <summary>
/// Read-only IT tools: ping, DNS, port test, traceroute (opens the tool), system/network/storage/process overviews.
/// Nothing here can end a process, change a service, the registry or delete anything.
/// </summary>
public abstract class ItCommandBase : OracleCommandBase
{
    public override string Module => "ittools";
    public override OracleEntity[] Entities => [OracleEntity.Tool];

    protected static OracleResult Unavailable(OracleCall c) => new() { Kind = OracleResultKind.Error, Headline = c.T("Orc_ToolUnavailable") };

    protected static OracleResult NeedsHost(OracleCall c, params string[] examples) =>
        new() { Kind = OracleResultKind.Clarify, Headline = c.T("Orc_NeedsHost"), Suggestions = examples.ToList(), Confidence = OracleConfidence.Medium };

    protected static OracleAction OpenTool(OracleCall c, string tab) =>
        new() { Label = c.T("Orc_OpenTool"), Icon = "ArrowRight", Kind = OracleActionKind.Navigate, Section = "ittools", Tab = tab };

    protected static bool ToolQuery(OracleParse p, string tool) => p.Tool == tool && p.Verb is null or "show" or "find" or "open";

    protected static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => (bytes / (double)(1L << 40)).ToString("0.0", CultureInfo.CurrentCulture) + " TB",
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        _ => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB",
    };
}

/// <summary>"ping 192.168.1.1", "ping google.com".</summary>
public sealed class PingCommand : ItCommandBase
{
    public override string Id => "it.ping";
    public override OracleIntent Intent => OracleIntent.Diagnose;
    public override string[] Examples => ["ping 192.168.1.1", "ping google.com"];
    public override string[] ExamplesAr => ["بينغ 1.1.1.1"];

    public override double Match(OracleParse p, OracleContext context) => ToolQuery(p, "ping") && (p.Host != null || p.Verb != "open") ? 0.95 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Ping == null) return Unavailable(c);
        if (c.P.Host == null) return NeedsHost(c, "ping 1.1.1.1", "ping google.com");
        if (!NetworkTarget.TryParse(c.P.Host, out var target, out var error)) return new OracleResult { Kind = OracleResultKind.Error, Headline = error ?? c.T("Orc_BadHost") };
        var replies = new List<PingReplyInfo>();
        await foreach (var r in c.S.Ping.PingAsync(target, 4, 1500, ct).ConfigureAwait(false)) replies.Add(r);
        var ok = replies.Where(r => r.Outcome == PingOutcome.Success && r.RoundTripMs != null).ToList();
        var loss = replies.Count == 0 ? 100 : 100 * (replies.Count - ok.Count) / replies.Count;
        var result = new OracleResult
        {
            Kind = OracleResultKind.Tool,
            Headline = ok.Count > 0 ? c.T("Orc_PingOk", target.Host, (long)ok.Average(r => r.RoundTripMs!.Value)) : c.T("Orc_PingFail", target.Host),
            Items = replies.Select(r => new OracleItem
            {
                Title = c.T("Orc_PingReply", r.Sequence, r.Address),
                Subtitle = r.Outcome == PingOutcome.Success ? "TTL " + r.Ttl : r.Detail ?? c.T("Orc_PingOutcome_" + r.Outcome),
                Value = r.RoundTripMs is { } ms ? ms + " ms" : "—",
                Icon = "Ping", Accent = r.Outcome == PingOutcome.Success ? "Green" : "Red", Kind = OracleEntity.Tool,
            }).ToList(),
            Metrics =
            [
                new(c.T("Orc_Avg"), ok.Count > 0 ? (long)ok.Average(r => r.RoundTripMs!.Value) + " ms" : "—", "Cyan"),
                new(c.T("Orc_Min"), ok.Count > 0 ? ok.Min(r => r.RoundTripMs!.Value) + " ms" : "—"),
                new(c.T("Orc_Max"), ok.Count > 0 ? ok.Max(r => r.RoundTripMs!.Value) + " ms" : "—"),
                new(c.T("Orc_Loss"), loss + " %", loss > 0 ? "Red" : "Green"),
            ],
            Actions = [RunAction(c.T("Orc_Again"), c.P.Input.Original, "Refresh"), OpenTool(c, "ping")],
        };
        return result;
    }
}

/// <summary>"dns google.com", "resolve arcadia.local".</summary>
public sealed class DnsCommand : ItCommandBase
{
    public override string Id => "it.dns";
    public override OracleIntent Intent => OracleIntent.Diagnose;
    public override string[] Examples => ["dns lookup google.com", "resolve github.com"];

    public override double Match(OracleParse p, OracleContext context) => ToolQuery(p, "dns") && (p.Host != null || p.Verb != "open") ? 0.95 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Dns == null) return Unavailable(c);
        if (c.P.Host == null) return NeedsHost(c, "dns lookup google.com");
        var a = await c.S.Dns.QueryAsync(c.P.Host, DnsRecordType.A, ct).ConfigureAwait(false);
        var aaaa = await c.S.Dns.QueryAsync(c.P.Host, DnsRecordType.AAAA, ct).ConfigureAwait(false);
        var records = a.Records.Concat(aaaa.Records).ToList();
        return new OracleResult
        {
            Kind = OracleResultKind.Tool,
            Headline = records.Count > 0 ? c.T("Orc_DnsOk", c.P.Host, records.Count) : c.T("Orc_DnsNone", c.P.Host),
            Items = records.Select(r => new OracleItem
            {
                Title = r.Value, Subtitle = r.Name, Value = r.Type + " · TTL " + r.Ttl, Icon = "Compass", Accent = "Cyan", Kind = OracleEntity.Tool,
                Actions = [new OracleAction { Label = c.T("Orc_Copy"), Icon = "Copy", Kind = OracleActionKind.Copy, Text = r.Value }, RunAction(c.T("Orc_Ping"), "ping " + r.Value, "Ping")],
            }).ToList(),
            Actions = [OpenTool(c, "dns")],
        };
    }
}

/// <summary>"test port 443 on google.com", "check port 3389 192.168.1.10".</summary>
public sealed class PortCommand : ItCommandBase
{
    public override string Id => "it.port";
    public override OracleIntent Intent => OracleIntent.Diagnose;
    public override string[] Examples => ["test port 443 on google.com", "check port 22 192.168.1.10"];

    public override double Match(OracleParse p, OracleContext context) => p.Tool == "port" && p.Verb is null or "show" or "find" or "open" or "set" && (p.Host != null || p.Verb != "open") ? 0.95 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        if (c.S.Ports == null) return Unavailable(c);
        if (c.P.Host == null || c.P.Port == null) return NeedsHost(c, "test port 443 on google.com");
        if (!NetworkTarget.TryParse(c.P.Host, out var target, out var error)) return new OracleResult { Kind = OracleResultKind.Error, Headline = error ?? c.T("Orc_BadHost") };
        var r = await c.S.Ports.TestAsync(target, c.P.Port.Value, 3000, ct).ConfigureAwait(false);
        var open = r.Outcome == PortTestOutcome.Reachable;
        return new OracleResult
        {
            Kind = OracleResultKind.Tool,
            Headline = c.T(open ? "Orc_PortOpen" : "Orc_PortClosed", r.Port, r.Host),
            Metrics = [new(c.T("Orc_Result"), c.T("Orc_PortOutcome_" + r.Outcome), open ? "Green" : "Red"), new(c.T("Orc_Address"), r.Address), new(c.T("Orc_Latency"), r.LatencyMs is { } ms ? ms + " ms" : "—")],
            Detail = r.Detail,
            Actions = [RunAction(c.T("Orc_Again"), c.P.Input.Original, "Refresh"), OpenTool(c, "port")],
        };
    }
}

/// <summary>"traceroute google.com": long-running, so it opens the Traceroute tool with the target.</summary>
public sealed class TraceCommand : ItCommandBase
{
    public override string Id => "it.trace";
    public override OracleIntent Intent => OracleIntent.Diagnose;
    public override string[] Examples => ["traceroute google.com"];

    public override double Match(OracleParse p, OracleContext context) => ToolQuery(p, "trace") && p.Host != null ? 0.95 : 0;

    public override Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct) => Task.FromResult(new OracleResult
    {
        Kind = OracleResultKind.Navigated,
        Headline = c.T("Orc_TraceOpening", c.P.Host),
        AutoAction = new OracleAction { Label = c.T("Orc_OpenTool"), Kind = OracleActionKind.Navigate, Section = "ittools", Tab = "trace", Text = c.P.Host },
    });
}

/// <summary>"system info", "specs", "show network adapters", "disk space", "top processes" — read-only overviews.</summary>
public sealed class SystemOverviewCommand : ItCommandBase
{
    public override string Id => "it.overview";
    public override OracleIntent Intent => OracleIntent.Show;
    public override string[] Examples => ["system info", "show my ip address", "disk space", "top processes"];
    public override string[] ExamplesDe => ["Systeminformationen", "Laufwerke"];
    public override string[] ExamplesAr => ["معلومات النظام", "التخزين"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Tool is "sysinfo" or "network" or "storage" or "processes" && p.Verb is null or "show" or "find" && p.Host == null ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        switch (c.P.Tool)
        {
            case "sysinfo":
            {
                if (c.S.SystemInfo == null) return Unavailable(c);
                var s = await c.S.SystemInfo.GetSnapshotAsync(ct).ConfigureAwait(false);
                return new OracleResult
                {
                    Kind = OracleResultKind.Tool,
                    Headline = s.ComputerName + " · " + s.WindowsEdition + (s.WindowsDisplayVersion is { } v ? " " + v : ""),
                    Metrics =
                    [
                        new("CPU", s.Cpu.Name, "Cyan"),
                        new(c.T("Orc_Cores"), $"{s.Cpu.PhysicalCores} / {s.Cpu.LogicalProcessors}"),
                        new("RAM", Size(s.InstalledMemoryBytes)),
                        new("GPU", string.Join(", ", s.Gpus.Select(g => g.Name))),
                        new(c.T("Orc_Uptime"), $"{(int)s.Uptime.TotalDays}d {s.Uptime.Hours}h {s.Uptime.Minutes}m"),
                        new("Build", s.BuildNumber + " · " + s.Architecture),
                    ],
                    Actions = [OpenTool(c, "sysinfo")],
                };
            }
            case "network":
            {
                if (c.S.Network == null) return Unavailable(c);
                var adapters = (await c.S.Network.GetAdaptersAsync(ct).ConfigureAwait(false)).Where(a => a.IsUp).OrderByDescending(a => a.HasGateway).ToList();
                return new OracleResult
                {
                    Kind = OracleResultKind.Tool,
                    Headline = c.T("Orc_NetworkHeadline", adapters.Count),
                    Items = adapters.Select(a => new OracleItem
                    {
                        Title = a.Name,
                        Subtitle = string.Join(" · ", new[] { a.Description, a.Gateways.Count > 0 ? "GW " + a.Gateways[0] : null, a.DnsServers.Count > 0 ? "DNS " + string.Join(", ", a.DnsServers.Take(2)) : null }.Where(x => x != null)),
                        Value = a.IPv4.Count > 0 ? $"{a.IPv4[0].Address}/{a.IPv4[0].Prefix}" : a.IPv6.FirstOrDefault() ?? "—",
                        Icon = "Network", Accent = a.HasGateway ? "Green" : "Blue", Kind = OracleEntity.Tool,
                        Actions = a.IPv4.Count > 0 ? [new OracleAction { Label = c.T("Orc_Copy"), Icon = "Copy", Kind = OracleActionKind.Copy, Text = a.IPv4[0].Address }] : [],
                    }).ToList(),
                    Actions = [OpenTool(c, "network")],
                };
            }
            case "storage":
            {
                if (c.S.Storage == null) return Unavailable(c);
                var drives = c.S.Storage.GetDrives().Where(d => d.IsReady).ToList();
                return new OracleResult
                {
                    Kind = OracleResultKind.Tool,
                    Headline = c.T("Orc_StorageHeadline", Size(drives.Sum(d => d.FreeBytes)), Size(drives.Sum(d => d.TotalBytes))),
                    Items = drives.Select(d => new OracleItem
                    {
                        Title = d.Name + (d.Label is { Length: > 0 } l ? " " + l : ""),
                        Subtitle = c.T("Orc_DriveFree", Size(d.FreeBytes)) + " · " + d.FileSystem,
                        Value = Size(d.UsedBytes) + " / " + Size(d.TotalBytes),
                        Progress = d.UsedFraction, Icon = "Drive", Accent = d.UsedFraction > 0.9 ? "Red" : d.UsedFraction > 0.75 ? "Amber" : "Cyan", Kind = OracleEntity.Tool,
                    }).ToList(),
                    Actions = [OpenTool(c, "storage")],
                };
            }
            default:
            {
                if (c.S.ListProcesses == null) return Unavailable(c);
                var list = await c.S.ListProcesses(ct).ConfigureAwait(false);
                var top = list.OrderByDescending(x => x.WorkingSetBytes).Take(12).ToList();
                return new OracleResult
                {
                    Kind = OracleResultKind.Tool,
                    Headline = c.T("Orc_ProcessesHeadline", list.Count),
                    Detail = c.T("Orc_ReadOnly"),
                    Items = top.Select(x => new OracleItem
                    {
                        Title = x.Name, Subtitle = "PID " + x.Id + " · " + c.T("Orc_Threads", x.Threads), Value = Size(x.WorkingSetBytes),
                        Icon = "Cpu", Accent = x.IsCritical ? "Amber" : "Blue", Kind = OracleEntity.Tool,
                    }).ToList(),
                    Actions = [OpenTool(c, "processes")],
                };
            }
        }
    }
}
