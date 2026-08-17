using System.Net;
using System.Net.Sockets;

namespace LMS.EdgeGateway.Core;

public static class EdgeGatewayIpAddress
{
    public static bool TryCanonicalize(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (!IPAddress.TryParse((value ?? string.Empty).Trim(), out var parsed))
        {
            return false;
        }

        address = Canonicalize(parsed);
        return true;
    }

    public static IPAddress Canonicalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address;
    }

    public static string CanonicalizeString(string? value)
    {
        return TryCanonicalize(value, out var address)
            ? address.ToString()
            : (value ?? string.Empty).Trim();
    }

    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var canonical = Canonicalize(address);
        return IPAddress.IsLoopback(canonical);
    }

    public static bool IsUniqueLocalIpv6(IPAddress address)
    {
        var canonical = Canonicalize(address);
        if (canonical.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var first = canonical.GetAddressBytes()[0];
        return (first & 0xfe) == 0xfc;
    }

    public static bool IsLanAddress(IPAddress address)
    {
        var canonical = Canonicalize(address);
        if (IPAddress.IsLoopback(canonical))
        {
            return true;
        }

        if (canonical.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = canonical.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return canonical.IsIPv6LinkLocal ||
               canonical.IsIPv6SiteLocal ||
               IsUniqueLocalIpv6(canonical);
    }

    public static bool AddressesEqual(IPAddress left, IPAddress right) =>
        Canonicalize(left).Equals(Canonicalize(right));
}
