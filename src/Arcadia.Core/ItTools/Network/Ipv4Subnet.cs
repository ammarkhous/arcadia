using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Arcadia.Core.ItTools.Network;

/// <summary>IPv4 subnet arithmetic (local only). /31 and /32 follow RFC 3021 conventions: every address is usable.</summary>
public sealed record Ipv4Subnet(IPAddress Address, int Prefix)
{
    public uint Mask => Prefix == 0 ? 0u : uint.MaxValue << (32 - Prefix);
    private uint Value => ToUInt(Address);
    public uint NetworkValue => Value & Mask;
    public uint BroadcastValue => NetworkValue | ~Mask;

    public IPAddress NetworkAddress => FromUInt(NetworkValue);
    public IPAddress BroadcastAddress => FromUInt(BroadcastValue);
    public IPAddress SubnetMask => FromUInt(Mask);
    public IPAddress WildcardMask => FromUInt(~Mask);

    public ulong TotalAddresses => 1UL << (32 - Prefix);

    public ulong UsableHosts => Prefix switch
    {
        32 => 1,
        31 => 2,
        _ => TotalAddresses - 2,
    };

    public IPAddress FirstUsable => Prefix >= 31 ? NetworkAddress : FromUInt(NetworkValue + 1);
    public IPAddress LastUsable => Prefix >= 31 ? BroadcastAddress : FromUInt(BroadcastValue - 1);

    public string Cidr => $"{NetworkAddress}/{Prefix.ToString(CultureInfo.InvariantCulture)}";

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToUInt(address) & Mask) == NetworkValue;

    /// <summary>Usable host addresses in order (for small subnets only).</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        if (Prefix >= 31)
        {
            for (ulong v = NetworkValue; v <= BroadcastValue; v++) yield return FromUInt((uint)v);
            yield break;
        }
        for (uint v = NetworkValue + 1; v < BroadcastValue; v++) yield return FromUInt(v);
    }

    /// <summary>Accepts "192.168.1.10/24", or an address plus a prefix ("24" or "/24") or a dotted mask ("255.255.255.0").</summary>
    public static bool TryParse(string? address, string? prefixOrMask, out Ipv4Subnet subnet, out string? error)
    {
        subnet = null!;
        error = null;
        address = address?.Trim() ?? string.Empty;
        prefixOrMask = prefixOrMask?.Trim().TrimStart('/') ?? string.Empty;

        var slash = address.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            if (prefixOrMask.Length == 0) prefixOrMask = address[(slash + 1)..];
            address = address[..slash];
        }

        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || address.Count(c => c == '.') != 3)
        {
            error = "address";
            return false;
        }

        int prefix;
        if (int.TryParse(prefixOrMask, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
        {
            if (p is < 0 or > 32) { error = "prefix"; return false; }
            prefix = p;
        }
        else if (IPAddress.TryParse(prefixOrMask, out var mask) && mask.AddressFamily == AddressFamily.InterNetwork && TryMaskToPrefix(ToUInt(mask), out var fromMask))
        {
            prefix = fromMask;
        }
        else
        {
            error = "prefix";
            return false;
        }

        subnet = new Ipv4Subnet(ip, prefix);
        return true;
    }

    /// <summary>A mask must be contiguous ones followed by zeros.</summary>
    public static bool TryMaskToPrefix(uint mask, out int prefix)
    {
        prefix = System.Numerics.BitOperations.PopCount(mask);
        return prefix == 0 ? mask == 0 : mask == uint.MaxValue << (32 - prefix);
    }

    public static uint ToUInt(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    public static IPAddress FromUInt(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
