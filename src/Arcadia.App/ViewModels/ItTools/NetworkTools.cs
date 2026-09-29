using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Diagnostics;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Network;
using Arcadia.Core.ItTools.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.ItTools;

/// <summary>Shared host input handling for the network tools.</summary>
public abstract partial class HostToolViewModel : ToolViewModel
{
    protected HostToolViewModel(ToolContext context) : base(context) => _host = context.Settings.Current.ItTools.LastHost ?? string.Empty;

    [ObservableProperty] private string _host;
    [ObservableProperty] private string? _hostError;

    protected bool TryGetTarget(out NetworkTarget target)
    {
        if (NetworkTarget.TryParse(Host, out target, out var error))
        {
            HostError = null;
            if (Context.Settings.Current.ItTools.LastHost != target.Host)
            {
                Context.Settings.Current.ItTools.LastHost = target.Host;
                _ = Context.Settings.SaveAsync();
            }
            return true;
        }
        HostError = Loc[error == "empty" ? "Net_ErrHostEmpty" : "Net_ErrHostInvalid"];
        return false;
    }

    protected bool TryNumber(string text, int min, int max, string errorKey, out int value)
    {
        if (ToolInput.TryParseInRange(text, min, max, out value)) return true;
        HostError = Loc.Format(errorKey, min, max);
        return false;
    }
}

// ═══════════════════════════ Network adapters ═══════════════════════════

public sealed partial class AdapterViewModel(NetworkAdapterInfo info, IReadOnlyList<InfoRow> rows, string statusText) : ObservableObject
{
    public NetworkAdapterInfo Info { get; } = info;
    public IReadOnlyList<InfoRow> Rows { get; } = rows;
    public string StatusText { get; } = statusText;
    public string Tone => Info.IsUp ? "Green" : "Steel";
    public string? PrimaryIp => Info.IPv4.Count > 0 ? Info.IPv4[0].Address : Info.IPv6.FirstOrDefault();
}

public sealed partial class NetworkAdaptersToolViewModel(ToolContext context, INetworkInformationService network) : ToolViewModel(context)
{
    private IReadOnlyList<NetworkAdapterInfo> _all = [];

    public override string Key => "network";
    public override string Icon => "Network";
    public override ToolCategory Category => ToolCategory.Network;

    [ObservableProperty] private IReadOnlyList<AdapterViewModel> _adapters = [];
    [ObservableProperty] private bool _showInactive;

    partial void OnShowInactiveChanged(bool value) => Build();

    public override async Task OnShownAsync()
    {
        if (!HasResult && !IsBusy) await RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        _all = await network.GetAdaptersAsync(ct);
        Build();
        HasResult = true;
    });

    protected override void OnCultureChanged() => Build();

    private void Build()
    {
        // By default only adapters that are up and actually carry an address (hides filter/QoS pseudo-adapters).
        Adapters = _all.Where(a => ShowInactive || (a.IsUp && (a.IPv4.Count > 0 || a.IPv6.Count > 0))).Select(a => new AdapterViewModel(a, Rows(a), Loc[a.IsUp ? "Net_Up" : "Net_Down"])).ToList();
        StatusText = Loc.Format("Net_AdapterCount", Adapters.Count, _all.Count);
    }

    private IReadOnlyList<InfoRow> Rows(NetworkAdapterInfo a)
    {
        static string Join(IEnumerable<string> items) => string.Join(Environment.NewLine, items) is { Length: > 0 } s ? s : "—";
        return
        [
            new(Loc["Net_Description"], a.Description),
            new(Loc["Net_Type"], a.Type),
            new(Loc["Net_Mac"], a.MacAddress ?? "—"),
            new(Loc["Net_IPv4"], Join(a.IPv4.Select(v => $"{v.Address}/{v.Prefix}"))),
            new(Loc["Net_Mask"], Join(a.IPv4.Select(v => v.Mask))),
            new(Loc["Net_IPv6"], Join(a.IPv6)),
            new(Loc["Net_Gateway"], Join(a.Gateways)),
            new(Loc["Net_Dns"], Join(a.DnsServers)),
            new(Loc["Net_Dhcp"], a.DhcpEnabled switch { true => Loc["It_Enabled"] + (a.DhcpServers.Count > 0 ? $" ({string.Join(", ", a.DhcpServers)})" : string.Empty), false => Loc["It_Disabled"], _ => "—" }),
            new(Loc["Net_Speed"], a.SpeedBitsPerSecond is { } s ? ByteSize.FormatBits(s, Culture) : "—"),
            new(Loc["Net_Suffix"], a.DnsSuffix ?? "—"),
        ];
    }

    [RelayCommand]
    private void CopyIp(AdapterViewModel? a)
    {
        if (a?.PrimaryIp is { } ip) Copy(ip, ip);
    }

    [RelayCommand]
    private void CopyMac(AdapterViewModel? a)
    {
        if (a?.Info.MacAddress is { } mac) Copy(mac, mac);
    }

    [RelayCommand]
    private void CopyAdapter(AdapterViewModel? a)
    {
        if (a != null) Copy(TextReportExporter.Render(AdapterSection(a)), a.Info.Name);
    }

    private ReportSection AdapterSection(AdapterViewModel a)
    {
        var s = new ReportSection($"{a.Info.Name} ({a.StatusText})");
        foreach (var r in a.Rows) s.Add(r.Label, r.Value);
        return s;
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var section = new ReportSection(Title);
        section.Children.AddRange(Adapters.Select(AdapterSection));
        return section;
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult()
    {
        var primary = Adapters.FirstOrDefault(a => a.Info.IsUp && a.Info.HasGateway);
        return (Title, primary == null ? Loc["It_Offline"] : $"{primary.Info.Name} · {primary.PrimaryIp} · {Loc["Net_Gateway"]} {primary.Info.Gateways.FirstOrDefault()}",
            primary == null ? DiagnosticOutcome.Warning : DiagnosticOutcome.Info);
    }
}

// ═══════════════════════════ Ping ═══════════════════════════

public sealed partial class PingToolViewModel(ToolContext context, IPingService ping) : HostToolViewModel(context)
{
    private readonly List<PingReplyInfo> _replies = [];
    private string? _target;

    public override string Key => "ping";
    public override string Icon => "Ping";
    public override ToolCategory Category => ToolCategory.Network;

    public ObservableCollection<ConsoleLine> Output { get; } = [];

    [ObservableProperty] private string _count = "4";
    [ObservableProperty] private string _timeout = "1000";
    [ObservableProperty] private string _sent = "0";
    [ObservableProperty] private string _received = "0";
    [ObservableProperty] private string _loss = "—";
    [ObservableProperty] private string _minimum = "—";
    [ObservableProperty] private string _maximum = "—";
    [ObservableProperty] private string _average = "—";
    [ObservableProperty] private string _lossTone = "Steel";
    [ObservableProperty] private IReadOnlyList<double> _latencies = [];

    [RelayCommand]
    private Task StartAsync()
    {
        if (!TryGetTarget(out var target)) return Task.CompletedTask;
        if (!TryNumber(Count, 1, 1000, "Net_ErrRange", out var count) || !TryNumber(Timeout, 100, 10000, "Net_ErrTimeout", out var timeout)) return Task.CompletedTask;

        return RunAsync(async ct =>
        {
            _replies.Clear();
            Output.Clear();
            HasResult = false;
            UpdateStats();
            _target = target.Host;
            if (!target.IsAddress)
            {
                var ip = await ping.ResolveAsync(target, ct);
                Output.Add(new ConsoleLine(Loc.Format("Ping_Header", target.Host, ip, 32), "Dim"));
            }
            else
            {
                Output.Add(new ConsoleLine(Loc.Format("Ping_HeaderIp", target.Host, 32), "Dim"));
            }
            StatusText = Loc["Ping_Running"];
            await foreach (var r in ping.PingAsync(target, count, timeout, ct))
            {
                _replies.Add(r);
                Output.Add(Line(r));
                UpdateStats();
                HasResult = true;
            }
            StatusText = Loc["It_Done"];
        }).ContinueWith(_ =>
        {
            if (_replies.Count > 0) Output.Add(new ConsoleLine(Summary(), "Accent"));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private ConsoleLine Line(PingReplyInfo r) => r.Outcome switch
    {
        PingOutcome.Success => new ConsoleLine(Loc.Format("Ping_Reply", r.Address, r.Bytes, r.RoundTripMs ?? 0, r.Ttl ?? 0), "Ok"),
        PingOutcome.TimedOut => new ConsoleLine(Loc["Ping_Timeout"], "Warn"),
        PingOutcome.Unreachable => new ConsoleLine(Loc.Format("Ping_Unreachable", r.Address), "Error"),
        _ => new ConsoleLine(Loc.Format("Ping_Failed", r.Detail ?? "?"), "Error"),
    };

    private void UpdateStats()
    {
        var s = PingStatistics.From(_replies);
        Sent = s.Sent.ToString(Culture);
        Received = s.Received.ToString(Culture);
        Loss = s.Sent == 0 ? "—" : Loc.Format("It_Percent", Math.Round(s.LossPercent));
        LossTone = s.Sent == 0 ? "Steel" : s.Lost == 0 ? "Green" : s.Received == 0 ? "Red" : "Amber";
        Minimum = Ms(s.Minimum);
        Maximum = Ms(s.Maximum);
        Average = s.Average is { } a ? Loc.Format("It_Ms", Math.Round(a)) : "—";
        Latencies = _replies.Select(r => (double)(r.RoundTripMs ?? 0)).ToList();
    }

    private string Summary()
    {
        var s = PingStatistics.From(_replies);
        return Loc.Format("Ping_Summary", s.Sent, s.Received, s.Lost, Math.Round(s.LossPercent), Minimum, Maximum, Average);
    }

    public override ReportSection? BuildReport()
    {
        if (_replies.Count == 0) return null;
        return new ReportSection($"{Title}: {_target}")
            .Add(Loc["Ping_Sent"], Sent).Add(Loc["Ping_Received"], Received).Add(Loc["Ping_Loss"], Loss)
            .Add(Loc["Ping_Min"], Minimum).Add(Loc["Ping_Max"], Maximum).Add(Loc["Ping_Avg"], Average)
            .With(s => s.Preformatted = string.Join(Environment.NewLine, Output.Select(l => l.Text)));
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult()
    {
        var s = PingStatistics.From(_replies);
        var outcome = s.Received == 0 ? DiagnosticOutcome.Failure : s.Lost > 0 ? DiagnosticOutcome.Warning : DiagnosticOutcome.Success;
        return ($"{Title} {_target}", Loc.Format("Ping_ShortSummary", s.Received, s.Sent, Average), outcome);
    }
}

// ═══════════════════════════ Traceroute ═══════════════════════════

public sealed record HopRow(int Hop, string Address, string HostName, string Probe1, string Probe2, string Probe3, string Status, string Tone);

public sealed partial class TracerouteToolViewModel(ToolContext context, ITracerouteService tracer) : HostToolViewModel(context)
{
    private string? _target;
    private bool _reached;

    public override string Key => "trace";
    public override string Icon => "Route";
    public override ToolCategory Category => ToolCategory.Network;

    public ObservableCollection<HopRow> Hops { get; } = [];

    [ObservableProperty] private string _maxHops = "30";
    [ObservableProperty] private string _timeout = "1000";
    [ObservableProperty] private bool _resolveNames = true;
    [ObservableProperty] private HopRow? _selected;

    [RelayCommand]
    private Task StartAsync()
    {
        if (!TryGetTarget(out var target)) return Task.CompletedTask;
        if (!TryNumber(MaxHops, 1, 64, "Net_ErrRange", out var hops) || !TryNumber(Timeout, 200, 5000, "Net_ErrTimeout", out var timeout)) return Task.CompletedTask;
        return RunAsync(async ct =>
        {
            Hops.Clear();
            HasResult = false;
            _reached = false;
            _target = target.Host;
            StatusText = Loc.Format("Trace_Running", target.Host);
            await foreach (var hop in tracer.TraceAsync(target, hops, timeout, ResolveNames, ct))
            {
                Hops.Add(ToRow(hop));
                HasResult = true;
                _reached |= hop.IsDestination;
            }
            StatusText = _reached ? Loc.Format("Trace_Reached", target.Host, Hops.Count) : Loc.Format("Trace_NotReached", Hops.Count);
        });
    }

    private HopRow ToRow(TraceHop h)
    {
        string P(int i) => i < h.RoundTrips.Count ? h.RoundTrips[i] is { } v ? Loc.Format("It_Ms", v) : "*" : "";
        var status = h.IsDestination ? Loc["Trace_Destination"] : h.TimedOut ? Loc["Trace_NoReply"] : Loc["Trace_Hop"];
        return new HopRow(h.Hop, h.Address ?? "*", h.HostName ?? string.Empty, P(0), P(1), P(2), status, h.IsDestination ? "Green" : h.TimedOut ? "Amber" : "Cyan");
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is { } h) Copy($"{h.Hop}  {h.Address}  {h.HostName}  {h.Probe1} {h.Probe2} {h.Probe3}".Trim(), h.Address);
    }

    public override ReportSection? BuildReport()
    {
        if (Hops.Count == 0) return null;
        var table = new ReportTable("#", Loc["Trace_Address"], Loc["Trace_Host"], "1", "2", "3", Loc["Trace_Status"]);
        foreach (var h in Hops) table.AddRow(h.Hop.ToString(CultureInfo.InvariantCulture), h.Address, h.HostName, h.Probe1, h.Probe2, h.Probe3, h.Status);
        return new ReportSection($"{Title}: {_target}") { Table = table }.Add(Loc["It_Result"], StatusText);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        ($"{Title} {_target}", StatusText, _reached ? DiagnosticOutcome.Success : DiagnosticOutcome.Warning);
}

// ═══════════════════════════ DNS lookup ═══════════════════════════

public sealed record DnsRow(string Type, string Name, string Value, string Ttl);

public sealed partial class DnsToolViewModel(ToolContext context, IDnsLookupService dns) : HostToolViewModel(context)
{
    private string? _query;

    public override string Key => "dns";
    public override string Icon => "Dns";
    public override ToolCategory Category => ToolCategory.Network;

    /// <summary>"ALL" runs the common record types one after another.</summary>
    public IReadOnlyList<string> RecordTypes { get; } = ["ALL", "A", "AAAA", "CNAME", "MX", "TXT", "NS", "PTR", "SOA"];

    [ObservableProperty] private string _recordType = "ALL";
    [ObservableProperty] private IReadOnlyList<DnsRow> _records = [];
    [ObservableProperty] private DnsRow? _selected;
    [ObservableProperty] private string? _notice;

    [RelayCommand]
    private void SetType(string? type)
    {
        if (type != null) RecordType = type;
    }

    [RelayCommand]
    private Task LookupAsync()
    {
        HostError = null;
        var text = Host.Trim();
        if (text.Length == 0) { HostError = Loc["Net_ErrHostEmpty"]; return Task.CompletedTask; }
        var isIp = System.Net.IPAddress.TryParse(text, out _);
        if (!isIp && !TryGetTarget(out _)) return Task.CompletedTask;
        DnsRecordType[] types = RecordType == "ALL"
            ? isIp ? [DnsRecordType.PTR] : new[] { DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.CNAME, DnsRecordType.MX, DnsRecordType.TXT, DnsRecordType.NS, DnsRecordType.SOA }
            : [Enum.Parse<DnsRecordType>(RecordType)];

        return RunAsync(async ct =>
        {
            Records = [];
            Notice = null;
            HasResult = false;
            _query = text;
            var rows = new List<DnsRow>();
            var failed = new List<string>();
            var elapsed = TimeSpan.Zero;
            foreach (var type in types)
            {
                StatusText = Loc.Format("Dns_Querying", type, text);
                DnsLookupResult result;
                try
                {
                    result = await dns.QueryAsync(text, type, ct);
                }
                catch (ToolException ex) when (types.Length > 1 && ex.Failure is not ToolFailure.NotFound and not ToolFailure.InvalidInput)
                {
                    // One record type failing (e.g. a server that mishandles TXT) should not hide the others.
                    Logger.LogWarning("DNS {Type} query for {Name} failed: {Message}", type, text, ex.Message);
                    failed.Add(type.ToString());
                    continue;
                }
                elapsed += result.Elapsed;
                rows.AddRange(result.Records
                    .Where(r => types.Length == 1 || r.Type == type || (r.Type == DnsRecordType.CNAME && type == DnsRecordType.A))
                    .Select(r => new DnsRow(r.Type.ToString(), r.Name, r.Value, r.Ttl.ToString(CultureInfo.InvariantCulture))));
            }
            Records = rows.DistinctBy(r => (r.Type, r.Name, r.Value)).ToList();
            HasResult = true;
            Notice = Records.Count == 0 ? Loc["Dns_NoRecords"] : null;
            StatusText = Loc.Format("Dns_Done", Records.Count, Math.Round(elapsed.TotalMilliseconds))
                         + (failed.Count > 0 ? " " + Loc.Format("Dns_SomeFailed", string.Join(", ", failed)) : string.Empty);
            if (failed.Count == types.Length) throw new ToolException(ToolFailure.DnsFailure, $"All DNS queries for {text} failed.");
        });
    }

    [RelayCommand]
    private void CopyValue(DnsRow? row)
    {
        if ((row ?? Selected) is { } r) Copy(r.Value, r.Value.Length > 40 ? r.Value[..40] + "…" : r.Value);
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var table = new ReportTable(Loc["Dns_Type"], Loc["Dns_Name"], Loc["Dns_Value"], "TTL");
        foreach (var r in Records) table.AddRow(r.Type, r.Name, r.Value, r.Ttl);
        return new ReportSection($"{Title}: {_query} ({RecordType})") { Table = table }.Add(Loc["It_Result"], Notice ?? StatusText);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        ($"{Title} {_query}", StatusText, Records.Count > 0 ? DiagnosticOutcome.Success : DiagnosticOutcome.Warning);
}

// ═══════════════════════════ Port tester ═══════════════════════════

public sealed record PortAttempt(string Time, string Host, string Address, int Port, string Outcome, string Latency, string Tone);

public sealed record CommonPort(int Port, string Name);

public sealed partial class PortTestToolViewModel(ToolContext context, IPortTestService tester) : HostToolViewModel(context)
{
    public override string Key => "port";
    public override string Icon => "Port";
    public override ToolCategory Category => ToolCategory.Network;

    /// <summary>Quick picks fill the port field; only one port is ever tested per click.</summary>
    public IReadOnlyList<CommonPort> CommonPorts { get; } =
    [
        new(80, "HTTP"), new(443, "HTTPS"), new(22, "SSH"), new(3389, "RDP"), new(445, "SMB"), new(53, "DNS"), new(25, "SMTP"), new(21, "FTP"), new(1433, "SQL"),
    ];

    public ObservableCollection<PortAttempt> History { get; } = [];

    [ObservableProperty] private string _port = "443";
    [ObservableProperty] private string _timeout = "3000";
    [ObservableProperty] private PortTestResult? _last;
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultDetail = string.Empty;
    [ObservableProperty] private string _resultTone = "Steel";

    [RelayCommand]
    private void UsePort(CommonPort? p)
    {
        if (p != null) Port = p.Port.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private Task TestAsync()
    {
        if (!TryGetTarget(out var target)) return Task.CompletedTask;
        if (!ToolInput.TryParsePort(Port, out var port)) { HostError = Loc["Port_ErrPort"]; return Task.CompletedTask; }
        if (!TryNumber(Timeout, 200, 30000, "Net_ErrTimeout", out var timeout)) return Task.CompletedTask;
        return RunAsync(async ct =>
        {
            StatusText = Loc.Format("Port_Testing", target.Host, port);
            var r = await tester.TestAsync(target, port, timeout, ct);
            Last = r;
            ResultTone = r.Outcome switch { PortTestOutcome.Reachable => "Green", PortTestOutcome.Refused => "Red", _ => "Amber" };
            ResultTitle = Loc[$"Port_{r.Outcome}"];
            ResultDetail = Loc.Format($"Port_{r.Outcome}_Detail", r.Host, r.Port, r.Address, r.LatencyMs ?? 0);
            History.Insert(0, new PortAttempt(DateTime.Now.ToString("T", Culture), r.Host, r.Address, r.Port, ResultTitle, Ms(r.LatencyMs), ResultTone));
            while (History.Count > 50) History.RemoveAt(History.Count - 1);
            HasResult = true;
            StatusText = null;
        });
    }

    public override ReportSection? BuildReport()
    {
        if (Last is not { } r) return null;
        var section = new ReportSection($"{Title}: {r.Host}:{r.Port}")
            .Add(Loc["Port_Host"], r.Host).Add(Loc["Port_Address"], r.Address).Add(Loc["Port_Port"], r.Port.ToString(CultureInfo.InvariantCulture))
            .Add(Loc["It_Result"], ResultTitle).Add(Loc["Port_Latency"], Ms(r.LatencyMs));
        if (History.Count > 1)
        {
            section.Table = new ReportTable(Loc["Port_Time"], Loc["Port_Host"], Loc["Port_Port"], Loc["It_Result"], Loc["Port_Latency"]);
            foreach (var h in History) section.Table.AddRow(h.Time, h.Host, h.Port.ToString(CultureInfo.InvariantCulture), h.Outcome, h.Latency);
        }
        return section;
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        ($"{Title} {Last?.Host}:{Last?.Port}", $"{ResultTitle} {(Last?.LatencyMs is { } l ? Ms(l) : string.Empty)}".Trim(),
            Last?.Outcome == PortTestOutcome.Reachable ? DiagnosticOutcome.Success : DiagnosticOutcome.Failure);
}

// ═══════════════════════════ Local devices ═══════════════════════════

public sealed record DeviceRow(string Address, string HostName, string Mac, string State, string Tone, string Latency, string Role);

public sealed partial class LocalDevicesToolViewModel(ToolContext context, ILocalNetworkScanner scanner) : ToolViewModel(context)
{
    public override string Key => "devices";
    public override string Icon => "Devices";
    public override ToolCategory Category => ToolCategory.Network;

    [ObservableProperty] private IReadOnlyList<DeviceRow> _devices = [];
    [ObservableProperty] private DeviceRow? _selected;
    [ObservableProperty] private string _subnetText = "—";
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _swept;

    public override async Task OnShownAsync()
    {
        if (!HasResult && !IsBusy) await LoadKnownAsync();
    }

    [RelayCommand]
    private Task LoadKnownAsync() => RunAsync(async ct =>
    {
        UpdateSubnet();
        Show(await scanner.GetKnownDevicesAsync(ct));
        Swept = false;
        StatusText = Loc.Format("Dev_Known", Devices.Count);
    });

    [RelayCommand]
    private Task ScanAsync()
    {
        UpdateSubnet();
        if (scanner.GetLocalSubnet() is { Prefix: < 22 } large)
        {
            Error = new ToolError(Loc["Dev_TooLarge"], Loc.Format("Dev_TooLarge_Hint", large.Cidr, large.UsableHosts.ToString("N0", Culture)), null, "Warning");
            return Task.CompletedTask;
        }
        return ScanCoreAsync();
    }

    private Task ScanCoreAsync() => RunAsync(async ct =>
    {
        Progress = 0;
        var progress = new Progress<LocalScanProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = Loc.Format("Dev_Progress", p.Done, p.Total);
        });
        StatusText = Loc["Dev_Scanning"];
        Show(await scanner.ScanSubnetAsync(progress, ct));
        Swept = true;
        ProgressText = string.Empty;
        StatusText = Loc.Format("Dev_Found", Devices.Count(d => d.Tone == "Green"), Devices.Count);
    });

    private void UpdateSubnet() => SubnetText = scanner.GetLocalSubnet() is { } s ? $"{s.Cidr}  ({s.UsableHosts.ToString("N0", Culture)} {Loc["Dev_Hosts"]})" : Loc["It_Offline"];

    private void Show(IReadOnlyList<LocalDevice> devices)
    {
        Devices = devices.Select(d => new DeviceRow(
            d.Address,
            d.HostName ?? string.Empty,
            d.MacAddress ?? "—",
            d.Reachable switch { true => Loc["Dev_Online"], false => Loc["Dev_Silent"], _ => Loc["Dev_Cached"] },
            d.Reachable switch { true => "Green", false => "Amber", _ => "Steel" },
            Ms(d.LatencyMs),
            d.IsThisComputer ? Loc["Dev_ThisPc"] : d.IsGateway ? Loc["Dev_Gateway"] : string.Empty)).ToList();
        HasResult = Devices.Count > 0;
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is { } d) Copy($"{d.Address}\t{d.HostName}\t{d.Mac}", d.Address);
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var table = new ReportTable(Loc["Dev_Ip"], Loc["Dev_Name"], Loc["Dev_Mac"], Loc["Dev_State"], Loc["Dev_Latency"], Loc["Dev_Role"]);
        foreach (var d in Devices) table.AddRow(d.Address, d.HostName, d.Mac, d.State, d.Latency, d.Role);
        return new ReportSection(Title) { Table = table }.Add(Loc["Dev_Subnet"], SubnetText).Add(Loc["It_Result"], StatusText);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() => (Title, StatusText, DiagnosticOutcome.Info);
}

// ═══════════════════════════ Subnet calculator ═══════════════════════════

public sealed partial class SubnetToolViewModel : ToolViewModel
{
    public SubnetToolViewModel(ToolContext context, INetworkInformationService network) : base(context)
    {
        var quick = network.GetQuickStatus();
        _address = quick.LocalIPv4 ?? "192.168.1.10";
        _prefix = quick.Prefix?.ToString(CultureInfo.InvariantCulture) ?? "24";
        Calculate();
    }

    public override string Key => "subnet";
    public override string Icon => "Subnet";
    public override ToolCategory Category => ToolCategory.Utilities;

    public IReadOnlyList<int> CommonPrefixes { get; } = [8, 16, 20, 22, 23, 24, 25, 26, 27, 28, 29, 30, 32];

    [ObservableProperty] private string _address;
    [ObservableProperty] private string _prefix;
    [ObservableProperty] private string? _inputError;
    [ObservableProperty] private IReadOnlyList<InfoRow> _rows = [];
    [ObservableProperty] private string _maskBinary = string.Empty;
    [ObservableProperty] private string _cidr = string.Empty;

    partial void OnAddressChanged(string value) => Calculate();
    partial void OnPrefixChanged(string value) => Calculate();

    protected override void OnCultureChanged() => Calculate();

    [RelayCommand]
    private void UsePrefix(int prefix) => Prefix = prefix.ToString(CultureInfo.InvariantCulture);

    private void Calculate()
    {
        if (!Ipv4Subnet.TryParse(Address, Prefix, out var s, out var error))
        {
            InputError = Loc[error == "address" ? "Subnet_ErrAddress" : "Subnet_ErrPrefix"];
            Rows = [];
            HasResult = false;
            return;
        }
        InputError = null;
        Cidr = s.Cidr;
        Rows =
        [
            new(Loc["Subnet_Network"], s.NetworkAddress.ToString()),
            new(Loc["Subnet_Broadcast"], s.Prefix >= 31 ? "—" : s.BroadcastAddress.ToString()),
            new(Loc["Subnet_Mask"], $"{s.SubnetMask}  (/{s.Prefix})"),
            new(Loc["Subnet_Wildcard"], s.WildcardMask.ToString()),
            new(Loc["Subnet_First"], s.FirstUsable.ToString()),
            new(Loc["Subnet_Last"], s.LastUsable.ToString()),
            new(Loc["Subnet_Total"], s.TotalAddresses.ToString("N0", Culture)),
            new(Loc["Subnet_Usable"], s.UsableHosts.ToString("N0", Culture)),
        ];
        MaskBinary = string.Join(".", Enumerable.Range(0, 4).Select(i => Convert.ToString((s.Mask >> (24 - i * 8)) & 0xFF, 2).PadLeft(8, '0')));
        HasResult = true;
    }

    public override ReportSection? BuildReport()
    {
        if (!HasResult) return null;
        var section = new ReportSection($"{Title}: {Cidr}");
        foreach (var r in Rows) section.Add(r.Label, r.Value);
        return section.Add(Loc["Subnet_Binary"], MaskBinary);
    }

    protected override (string Title, string? Summary, DiagnosticOutcome Outcome) DescribeResult() =>
        ($"{Title} {Cidr}", Rows.Count > 7 ? $"{Rows[4].Value} – {Rows[5].Value} · {Rows[7].Value} {Loc["Subnet_UsableShort"]}" : null, DiagnosticOutcome.Info);
}

internal static class ReportSectionExtensions
{
    public static ReportSection With(this ReportSection section, Action<ReportSection> change)
    {
        change(section);
        return section;
    }
}
