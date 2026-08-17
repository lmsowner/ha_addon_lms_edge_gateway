using System.Net;

namespace LMS.EdgeGateway.Core;

public static class EdgeGatewayListenAccess
{
    private static readonly (IPAddress Network, int Prefix)[] AllowedNetworks =
    [
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("172.30.32.0"), 23),
        (IPAddress.Parse("172.30.232.0"), 23),
        (IPAddress.Parse("fe80::"), 10)
    ];

    public static bool IsAllowedRemoteAddress(IPAddress? remoteIpAddress)
    {
        if (remoteIpAddress is null)
        {
            return false;
        }

        var address = EdgeGatewayIpAddress.Canonicalize(remoteIpAddress);
        if (EdgeGatewayIpAddress.IsLoopback(address))
        {
            return true;
        }

        return AllowedNetworks.Any(network => AddressInCidr(address, network.Network, network.Prefix));
    }

    private static bool AddressInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        address = EdgeGatewayIpAddress.Canonicalize(address);
        network = EdgeGatewayIpAddress.Canonicalize(network);
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var maxPrefix = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
