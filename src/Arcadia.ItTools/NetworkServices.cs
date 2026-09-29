using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Network;
using Microsoft.Extensions.Logging;

namespace Arcadia.ItTools;

public sealed class NetworkInformationService : INetworkInformationService
{
    public Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<NetworkAdapterInfo>>(() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(ToInfo)
            .OrderByDescending(a => a.IsUp)
            .ThenByDescending(a => a.HasGateway)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(), cancellationToken);

    public NetworkQuickStatus GetQuickStatus()
    {
        try
        {
            var primary = PrimaryInterface();
            if (primary == null) return new NetworkQuickStatus(NetworkInterface.GetIsNetworkAvailable(), null, null, null);
            var v4 = primary.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            return new NetworkQuickStatus(true, primary.Name, v4?.Address.ToString(), v4?.PrefixLength);
        }
        catch (NetworkInformationException)
        {
            return new NetworkQuickStatus(false, null, null, null);
        }
    }

    /// <summary>The adapter carrying the default route: up, with an IPv4 gateway.</summary>
    internal static NetworkInterface? PrimaryInterface() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork));

    private static NetworkAdapterInfo ToInfo(NetworkInterface n)
    {
        var p = n.GetIPProperties();
        bool? dhcp = null;
        try { dhcp = p.GetIPv4Properties()?.IsDhcpEnabled; }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }

        var mac = n.GetPhysicalAddress().GetAddressBytes();
        return new NetworkAdapterInfo(
            n.Id,
            n.Name,
            n.Description,
            n.NetworkInterfaceType.ToString(),
            n.OperationalStatus == OperationalStatus.Up,
            n.OperationalStatus.ToString(),
            mac.Length == 0 ? null : string.Join("-", mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))),
            p.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => (u.Address.ToString(), u.IPv4Mask?.ToString() ?? string.Empty, u.PrefixLength)).ToList(),
            p.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(u => u.Address.ToString()).ToList(),
            p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any)).Select(a => a.ToString()).ToList(),
            p.DnsAddresses.Select(a => a.ToString()).ToList(),
            dhcp,
            p.DhcpServerAddresses.Select(a => a.ToString()).ToList(),
            n.Speed > 0 ? n.Speed : null,
            string.IsNullOrWhiteSpace(p.DnsSuffix) ? null : p.DnsSuffix);
    }
}

internal static class Resolver
{
    /// <summary>Resolves a target to one address (IPv4 preferred), mapping failures to <see cref="ToolFailure.DnsFailure"/>.</summary>
    public static async Task<IPAddress> ResolveAsync(NetworkTarget target, CancellationToken ct)
    {
        if (target.Address is { } ip) return ip;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault()
                   ?? throw new ToolException(ToolFailure.DnsFailure, $"'{target.Host}' has no address.");
        }
        catch (SocketException ex)
        {
            throw new ToolException(ToolFailure.DnsFailure, $"'{target.Host}' could not be resolved ({ex.SocketErrorCode}).", ex);
        }
    }

    public static async Task<string?> ReverseAsync(string address, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address, ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
            return string.Equals(entry.HostName, address, StringComparison.Ordinal) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed class PingService : IPingService
{
    private static readonly byte[] Payload = Enumerable.Range(0, 32).Select(i => (byte)('a' + i % 23)).ToArray();

    public Task<IPAddress> ResolveAsync(NetworkTarget target, CancellationToken cancellationToken = default) => Resolver.ResolveAsync(target, cancellationToken);

    public async IAsyncEnumerable<PingReplyInfo> PingAsync(NetworkTarget target, int count, int timeoutMs, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        using var ping = new Ping();
        for (int seq = 1; seq <= count; seq++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.StartNew();
            PingReplyInfo reply;
            try
            {
                var r = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), Payload, new PingOptions(128, dontFragment: true), cancellationToken).ConfigureAwait(false);
                reply = r.Status switch
                {
                    IPStatus.Success => new PingReplyInfo(seq, r.Address.ToString(), PingOutcome.Success, r.RoundtripTime, r.Options?.Ttl, r.Buffer.Length),
                    IPStatus.TimedOut => new PingReplyInfo(seq, address.ToString(), PingOutcome.TimedOut, null, null, Payload.Length),
                    IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable
                        => new PingReplyInfo(seq, r.Address?.ToString() ?? address.ToString(), PingOutcome.Unreachable, null, null, Payload.Length, r.Status.ToString()),
                    _ => new PingReplyInfo(seq, address.ToString(), PingOutcome.Failed, null, null, Payload.Length, r.Status.ToString()),
                };
            }
            catch (PingException ex)
            {
                reply = new PingReplyInfo(seq, address.ToString(), PingOutcome.Failed, null, null, Payload.Length, ex.InnerException?.Message ?? ex.Message);
            }
            yield return reply;

            // Like ping.exe: one request per second.
            if (seq < count)
            {
                var wait = 1000 - (int)started.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

public sealed class TracerouteService : ITracerouteService
{
    private const int ProbesPerHop = 3;
    private static readonly byte[] Payload = new byte[32];

    public async IAsyncEnumerable<TraceHop> TraceAsync(NetworkTarget target, int maxHops, int timeoutMs, bool resolveNames, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var destination = await Resolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        using var ping = new Ping();
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            var times = new List<long?>();
            IPAddress? hopAddress = null;
            bool reached = false;
            for (int probe = 0; probe < ProbesPerHop; probe++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                try
                {
                    var r = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs), Payload, new PingOptions(ttl, dontFragment: true), cancellationToken).ConfigureAwait(false);
                    if (r.Status is IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.Success)
                    {
                        hopAddress ??= r.Address;
                        // RoundtripTime is 0 for TTL-expired replies; measure it ourselves.
                        times.Add(r.Status == IPStatus.Success && r.RoundtripTime > 0 ? r.RoundtripTime : sw.ElapsedMilliseconds);
                        reached |= r.Status == IPStatus.Success;
                    }
                    else
                    {
                        times.Add(null);
                    }
                }
                catch (PingException)
                {
                    times.Add(null);
                }
            }
            string? name = null;
            if (resolveNames && hopAddress != null)
                name = await Resolver.ReverseAsync(hopAddress.ToString(), 1500, cancellationToken).ConfigureAwait(false);
            yield return new TraceHop(ttl, hopAddress?.ToString(), name, times, reached);
            if (reached) yield break;
        }
    }
}

public sealed class PortTestService : IPortTestService
{
    public async Task<PortTestResult> TestAsync(NetworkTarget target, int port, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var address = await Resolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
            var latency = sw.ElapsedMilliseconds;
            socket.Shutdown(SocketShutdown.Both);
            return new PortTestResult(target.Host, address.ToString(), port, PortTestOutcome.Reachable, latency, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PortTestResult(target.Host, address.ToString(), port, PortTestOutcome.TimedOut, null, null);
        }
        catch (SocketException ex)
        {
            var outcome = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => PortTestOutcome.Refused,
                SocketError.TimedOut => PortTestOutcome.TimedOut,
                _ => PortTestOutcome.Unreachable,
            };
            return new PortTestResult(target.Host, address.ToString(), port, outcome, outcome == PortTestOutcome.Refused ? sw.ElapsedMilliseconds : null, ex.SocketErrorCode.ToString());
        }
    }
}

/// <summary>
/// Local device discovery for the user's own network: the Windows neighbour cache, plus an optional gentle
/// ping sweep of the local subnet. No port scanning, no probing of services.
/// </summary>
public sealed class LocalNetworkScanner(ILogger<LocalNetworkScanner> logger) : ILocalNetworkScanner
{
    private const int MaxSweepPrefix = 22; // at most 1,022 hosts
    private const int Parallelism = 24;
    private const int PingTimeoutMs = 700;

    public Ipv4Subnet? GetLocalSubnet()
    {
        var nic = NetworkInformationService.PrimaryInterface();
        var v4 = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
        return v4 == null ? null : new Ipv4Subnet(v4.Address, v4.PrefixLength);
    }

    public Task<IReadOnlyList<LocalDevice>> GetKnownDevicesAsync(CancellationToken cancellationToken = default) =>
        BuildAsync(GetLocalSubnet(), new Dictionary<string, long?>(), cancellationToken);

    public async Task<IReadOnlyList<LocalDevice>> ScanSubnetAsync(IProgress<LocalScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        var subnet = GetLocalSubnet() ?? throw new ToolException(ToolFailure.Unavailable, "No active IPv4 network.");
        if (subnet.Prefix < MaxSweepPrefix)
            throw new ToolException(ToolFailure.InvalidInput, $"The local network {subnet.Cidr} is too large to sweep (limit /{MaxSweepPrefix}).");

        var hosts = subnet.Hosts().ToList();
        var replies = new System.Collections.Concurrent.ConcurrentDictionary<string, long?>();
        int done = 0;
        logger.LogInformation("Sweeping local subnet {Subnet} ({Count} addresses)", subnet.Cidr, hosts.Count);
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (host, ct) =>
        {
            using var ping = new Ping();
            try
            {
                var r = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(PingTimeoutMs), new byte[16], null, ct).ConfigureAwait(false);
                if (r.Status == IPStatus.Success) replies[host.ToString()] = r.RoundtripTime;
            }
            catch (PingException) { }
            progress?.Report(new LocalScanProgress(Interlocked.Increment(ref done), hosts.Count));
        }).ConfigureAwait(false);

        return await BuildAsync(subnet, replies, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LocalDevice>> BuildAsync(Ipv4Subnet? subnet, IReadOnlyDictionary<string, long?> replies, CancellationToken ct)
    {
        if (subnet == null) return [];
        var nic = NetworkInformationService.PrimaryInterface();
        var gateway = nic?.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        var self = subnet.Address.ToString();
        var selfMac = nic?.GetPhysicalAddress().GetAddressBytes() is { Length: > 0 } m ? FormatMac(m) : null;

        var neighbours = ReadNeighbourTable().Where(n => subnet.Contains(IPAddress.Parse(n.Address)) && !IsBroadcast(n.Address, subnet)).ToDictionary(n => n.Address, n => n.Mac);
        var addresses = neighbours.Keys.Union(replies.Keys).Append(self).Distinct().ToList();

        var devices = await Task.WhenAll(addresses.Select(async a =>
        {
            var name = a == self ? Environment.MachineName : await Resolver.ReverseAsync(a, 1200, ct).ConfigureAwait(false);
            bool answered = replies.TryGetValue(a, out var rtt);
            return new LocalDevice(a, name, a == self ? selfMac : neighbours.GetValueOrDefault(a),
                a == self ? true : answered ? true : replies.Count > 0 ? false : null,
                answered ? rtt : null, a == gateway, a == self);
        })).ConfigureAwait(false);

        return devices.OrderBy(d => Ipv4Subnet.ToUInt(IPAddress.Parse(d.Address))).ToList();
    }

    private static bool IsBroadcast(string address, Ipv4Subnet subnet) =>
        address == subnet.BroadcastAddress.ToString() || address == "255.255.255.255";

    internal static unsafe List<(string Address, string? Mac)> ReadNeighbourTable()
    {
        int size = 0;
        Native.GetIpNetTable(0, ref size, false);
        if (size <= 0) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetIpNetTable(buffer, ref size, true) != 0) return [];
            int count = Marshal.ReadInt32(buffer);
            var rows = (Native.MibIpNetRow*)(buffer + 4);
            var list = new List<(string, string?)>(count);
            for (int i = 0; i < count; i++)
            {
                var row = rows[i];
                if (row.Type == 2) continue; // invalid
                var ip = new IPAddress(row.Addr);
                var first = ip.GetAddressBytes()[0];
                if (first is >= 224 or 0) continue; // multicast / reserved
                var mac = new byte[Math.Clamp(row.PhysAddrLen, 0, 8)];
                for (int k = 0; k < mac.Length; k++) mac[k] = row.PhysAddr[k];
                if (mac.Length == 0 || mac.All(b => b == 0) || mac.All(b => b == 0xFF)) continue;
                list.Add((ip.ToString(), FormatMac(mac)));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string FormatMac(byte[] mac) => string.Join("-", mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
