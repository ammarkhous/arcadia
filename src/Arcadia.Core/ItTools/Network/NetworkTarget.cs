using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Arcadia.Core.ItTools.Network;

public enum NetworkTargetKind { IPv4, IPv6, HostName }

/// <summary>
/// A host the user typed into a network tool, normalised: surrounding whitespace, a URL scheme, a path,
/// an IPv6 zone in brackets or a trailing dot are tolerated so pasted text "just works".
/// </summary>
public sealed partial record NetworkTarget(string Host, NetworkTargetKind Kind, IPAddress? Address)
{
    private const int MaxHostLength = 253;

    public bool IsAddress => Address != null;

    public override string ToString() => Host;

    public static bool TryParse(string? input, out NetworkTarget target, out string? error)
    {
        target = null!;
        error = null;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) { error = "empty"; return false; }

        // Pasted URLs: https://host:port/path → host
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" && !string.IsNullOrEmpty(uri.Host))
            text = uri.IdnHost.Trim('[', ']');
        else if (text.StartsWith('[') && text.Contains(']', StringComparison.Ordinal))
            text = text[1..text.IndexOf(']', StringComparison.Ordinal)];

        if (IPAddress.TryParse(text, out var ip) && LooksLikeAddress(text, ip))
        {
            target = new NetworkTarget(ip.ToString(), ip.AddressFamily == AddressFamily.InterNetworkV6 ? NetworkTargetKind.IPv6 : NetworkTargetKind.IPv4, ip);
            return true;
        }

        var host = text.TrimEnd('.');
        try
        {
            host = new IdnMapping().GetAscii(host); // internationalised names → punycode
        }
        catch (ArgumentException)
        {
            error = "invalid";
            return false;
        }
        if (host.Length is 0 or > MaxHostLength || !host.Split('.').All(IsLabel) || host.All(c => char.IsDigit(c) || c == '.'))
        {
            error = "invalid";
            return false;
        }
        target = new NetworkTarget(host.ToLowerInvariant(), NetworkTargetKind.HostName, null);
        return true;
    }

    /// <summary>IPAddress.TryParse accepts "1" or "1.2" (legacy forms); only full dotted quads or IPv6 count as addresses.</summary>
    private static bool LooksLikeAddress(string text, IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 || text.Count(c => c == '.') == 3;

    private static bool IsLabel(string label) => label.Length is > 0 and <= 63 && LabelPattern().IsMatch(label);

    [GeneratedRegex("^[A-Za-z0-9_]([A-Za-z0-9_-]*[A-Za-z0-9_])?$")]
    private static partial Regex LabelPattern();

    /// <summary>The reverse-lookup (PTR) name for an address: 1.2.3.4 → 4.3.2.1.in-addr.arpa.</summary>
    public static string ReverseLookupName(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return string.Join('.', bytes.Reverse().Select(b => b.ToString(CultureInfo.InvariantCulture))) + ".in-addr.arpa";
        var nibbles = bytes.SelectMany(b => new[] { b >> 4, b & 0xF }).Reverse().Select(n => n.ToString("x", CultureInfo.InvariantCulture));
        return string.Join('.', nibbles) + ".ip6.arpa";
    }
}

/// <summary>Validation helpers for tool inputs.</summary>
public static class ToolInput
{
    public static bool TryParsePort(string? text, out int port) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    public static bool TryParseInRange(string? text, int min, int max, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
}
