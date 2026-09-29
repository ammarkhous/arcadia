using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Arcadia.Core.ItTools;
using Arcadia.Core.ItTools.Network;

namespace Arcadia.ItTools;

/// <summary>
/// DNS queries through the Windows resolver (DnsQuery_W), so results match what Windows itself sees —
/// including MX, TXT, NS and SOA records that System.Net.Dns cannot return.
/// </summary>
public sealed class DnsLookupService : IDnsLookupService
{
    private const int ErrorSuccess = 0;
    private const int DnsErrorNameError = 9003;   // NXDOMAIN
    private const int DnsInfoNoRecords = 9501;
    private const int DnsErrorServerFailure = 9002;
    private const int DnsErrorRefused = 9005;
    private const int ErrorTimeout = 1460;
    private const int ErrorInvalidName = 123;
    private const int DnsErrorInvalidName = 9560;
    private const int DnsQueryStandard = 0;
    private const int DnsQueryUseTcpOnly = 0x2;
    private const int DnsErrorBadPacket = 9502;
    private const int DnsFreeRecordList = 1;
    private const int DataOffset = 32; // DNS_RECORD header size on 64-bit

    public Task<DnsLookupResult> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default)
    {
        var query = name.Trim().TrimEnd('.');
        if (type == DnsRecordType.PTR && IPAddress.TryParse(query, out var ip)) query = NetworkTarget.ReverseLookupName(ip);
        // DnsQuery_W cannot be cancelled; abandon it on cancellation (it finishes on its own shortly after).
        return Task.Run(() => Query(query, type), CancellationToken.None).WaitAsync(cancellationToken);
    }

    private static DnsLookupResult Query(string name, DnsRecordType type)
    {
        var sw = Stopwatch.StartNew();
        int status = Native.DnsQuery(name, TypeCode(type), DnsQueryStandard, 0, out var list, 0);
        // A malformed/truncated UDP answer happens now and then (seen with DNS proxies such as Hyper-V's):
        // retry twice, then ask over TCP as a last resort.
        for (int attempt = 0; status == DnsErrorBadPacket && attempt < 3; attempt++)
        {
            if (list != 0) Native.DnsRecordListFree(list, DnsFreeRecordList);
            list = 0;
            Thread.Sleep(80 * (attempt + 1));
            status = Native.DnsQuery(name, TypeCode(type), (uint)(attempt < 2 ? DnsQueryStandard : DnsQueryUseTcpOnly), 0, out list, 0);
        }
        try
        {
            switch (status)
            {
                case ErrorSuccess: break;
                case DnsInfoNoRecords: return new DnsLookupResult(name, type, [], sw.Elapsed);
                case DnsErrorNameError: throw new ToolException(ToolFailure.NotFound, $"'{name}' does not exist (NXDOMAIN).");
                case ErrorTimeout: throw new ToolException(ToolFailure.Timeout, "The DNS server did not answer in time.");
                case DnsErrorServerFailure or DnsErrorRefused: throw new ToolException(ToolFailure.DnsFailure, $"The DNS server could not answer (code {status}).");
                case ErrorInvalidName or DnsErrorInvalidName: throw new ToolException(ToolFailure.InvalidInput, $"'{name}' is not a valid DNS name.");
                default: throw new ToolException(ToolFailure.DnsFailure, $"DNS query failed (code {status}).");
            }

            var records = new List<DnsRecordInfo>();
            for (var p = list; p != 0; p = Marshal.ReadIntPtr(p))
            {
                var flags = (uint)Marshal.ReadInt32(p, 20);
                if ((flags & 0x3) != 1) continue; // answer section only
                var recordType = (ushort)Marshal.ReadInt16(p, 16);
                var owner = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, 8)) ?? name;
                var ttl = (uint)Marshal.ReadInt32(p, 24);
                if (FromCode(recordType) is { } kind && Read(p + DataOffset, kind) is { } value)
                    records.Add(new DnsRecordInfo(kind, owner, value, ttl));
            }
            return new DnsLookupResult(name, type, records, sw.Elapsed);
        }
        finally
        {
            if (list != 0) Native.DnsRecordListFree(list, DnsFreeRecordList);
        }
    }

    private static string? Read(nint data, DnsRecordType kind)
    {
        switch (kind)
        {
            case DnsRecordType.A:
                return new IPAddress((uint)Marshal.ReadInt32(data)).ToString();
            case DnsRecordType.AAAA:
                var bytes = new byte[16];
                Marshal.Copy(data, bytes, 0, 16);
                return new IPAddress(bytes).ToString();
            case DnsRecordType.CNAME or DnsRecordType.NS or DnsRecordType.PTR:
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(data));
            case DnsRecordType.MX:
                var exchange = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data));
                var preference = (ushort)Marshal.ReadInt16(data, IntPtr.Size);
                return $"{preference} {exchange}";
            case DnsRecordType.TXT:
                int count = Marshal.ReadInt32(data);
                var parts = new List<string>(count);
                for (int i = 0; i < count; i++) parts.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, IntPtr.Size + i * IntPtr.Size)) ?? string.Empty);
                return string.Concat(parts);
            case DnsRecordType.SOA:
                var primary = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data));
                var admin = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, IntPtr.Size));
                var serial = (uint)Marshal.ReadInt32(data, IntPtr.Size * 2);
                return $"{primary} {admin} serial {serial}";
            default:
                return null;
        }
    }

    private static ushort TypeCode(DnsRecordType type) => type switch
    {
        DnsRecordType.A => 1,
        DnsRecordType.NS => 2,
        DnsRecordType.CNAME => 5,
        DnsRecordType.SOA => 6,
        DnsRecordType.PTR => 12,
        DnsRecordType.MX => 15,
        DnsRecordType.TXT => 16,
        DnsRecordType.AAAA => 28,
        _ => 1,
    };

    private static DnsRecordType? FromCode(ushort code) => code switch
    {
        1 => DnsRecordType.A,
        2 => DnsRecordType.NS,
        5 => DnsRecordType.CNAME,
        6 => DnsRecordType.SOA,
        12 => DnsRecordType.PTR,
        15 => DnsRecordType.MX,
        16 => DnsRecordType.TXT,
        28 => DnsRecordType.AAAA,
        _ => null,
    };
}
