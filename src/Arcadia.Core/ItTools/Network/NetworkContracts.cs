using System.Net;

namespace Arcadia.Core.ItTools.Network;

// ─────────────────────────── adapters ───────────────────────────

public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string Type,
    bool IsUp,
    string Status,
    string? MacAddress,
    IReadOnlyList<(string Address, string Mask, int Prefix)> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    bool? DhcpEnabled,
    IReadOnlyList<string> DhcpServers,
    long? SpeedBitsPerSecond,
    string? DnsSuffix)
{
    public bool HasGateway => Gateways.Count > 0;
}

/// <summary>Cheap summary for status strips.</summary>
public sealed record NetworkQuickStatus(bool IsConnected, string? AdapterName, string? LocalIPv4, int? Prefix);

public interface INetworkInformationService
{
    Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default);
    NetworkQuickStatus GetQuickStatus();
}

// ─────────────────────────── ping ───────────────────────────

public enum PingOutcome { Success, TimedOut, Unreachable, Failed }

public sealed record PingReplyInfo(int Sequence, string Address, PingOutcome Outcome, long? RoundTripMs, int? Ttl, int Bytes, string? Detail = null);

public interface IPingService
{
    /// <summary>Resolves the target (IPv4 preferred) and yields one reply per echo request.</summary>
    IAsyncEnumerable<PingReplyInfo> PingAsync(NetworkTarget target, int count, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>Resolves a host name to the address a ping would use.</summary>
    Task<IPAddress> ResolveAsync(NetworkTarget target, CancellationToken cancellationToken = default);
}

/// <summary>Summary of a ping run, computed like ping.exe does (only successful replies count for latency).</summary>
public sealed record PingStatistics(int Sent, int Received, long? Minimum, long? Maximum, double? Average)
{
    public int Lost => Sent - Received;
    public double LossPercent => Sent == 0 ? 0 : Lost * 100.0 / Sent;

    public static PingStatistics From(IEnumerable<PingReplyInfo> replies)
    {
        var list = replies as IReadOnlyCollection<PingReplyInfo> ?? replies.ToList();
        var times = list.Where(r => r.Outcome == PingOutcome.Success && r.RoundTripMs != null).Select(r => r.RoundTripMs!.Value).ToList();
        return new PingStatistics(list.Count, times.Count,
            times.Count > 0 ? times.Min() : null,
            times.Count > 0 ? times.Max() : null,
            times.Count > 0 ? times.Average() : null);
    }
}

// ─────────────────────────── traceroute ───────────────────────────

public sealed record TraceHop(int Hop, string? Address, string? HostName, IReadOnlyList<long?> RoundTrips, bool IsDestination)
{
    public bool TimedOut => Address == null;
}

public interface ITracerouteService
{
    IAsyncEnumerable<TraceHop> TraceAsync(NetworkTarget target, int maxHops, int timeoutMs, bool resolveNames, CancellationToken cancellationToken = default);
}

// ─────────────────────────── DNS ───────────────────────────

public enum DnsRecordType { A, AAAA, CNAME, MX, TXT, NS, PTR, SOA }

public sealed record DnsRecordInfo(DnsRecordType Type, string Name, string Value, uint Ttl);

public sealed record DnsLookupResult(string Query, DnsRecordType Type, IReadOnlyList<DnsRecordInfo> Records, TimeSpan Elapsed);

public interface IDnsLookupService
{
    /// <summary>Queries the system's DNS resolver. A missing name raises <see cref="ToolFailure.NotFound"/>; no records of the type returns an empty list.</summary>
    Task<DnsLookupResult> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default);
}

// ─────────────────────────── ports ───────────────────────────

public enum PortTestOutcome { Reachable, Refused, TimedOut, Unreachable }

public sealed record PortTestResult(string Host, string Address, int Port, PortTestOutcome Outcome, long? LatencyMs, string? Detail);

public interface IPortTestService
{
    /// <summary>One TCP connection attempt (no scanning).</summary>
    Task<PortTestResult> TestAsync(NetworkTarget target, int port, int timeoutMs, CancellationToken cancellationToken = default);
}

// ─────────────────────────── local devices ───────────────────────────

public sealed record LocalDevice(string Address, string? HostName, string? MacAddress, bool? Reachable, long? LatencyMs, bool IsGateway, bool IsThisComputer);

public sealed record LocalScanProgress(int Done, int Total);

public interface ILocalNetworkScanner
{
    /// <summary>The IPv4 subnet of the active adapter, if any.</summary>
    Ipv4Subnet? GetLocalSubnet();

    /// <summary>Devices Windows already knows about (the neighbour/ARP cache), without sending traffic.</summary>
    Task<IReadOnlyList<LocalDevice>> GetKnownDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pings each address of the local subnet once (gently, with limited parallelism) and returns the devices
    /// that answered or are in the neighbour cache. Refuses subnets larger than /22.
    /// </summary>
    Task<IReadOnlyList<LocalDevice>> ScanSubnetAsync(IProgress<LocalScanProgress>? progress, CancellationToken cancellationToken = default);
}
