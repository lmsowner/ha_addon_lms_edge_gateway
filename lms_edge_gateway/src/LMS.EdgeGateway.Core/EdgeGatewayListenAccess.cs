using System.Net;

namespace LMS.EdgeGateway.Core;

public static class EdgeGatewayListenAccess
{
    public static bool IsAllowedRemoteAddress(IPAddress? remoteIpAddress)
    {
        if (remoteIpAddress is null)
        {
            return true;
        }

        var address = EdgeGatewayIpAddress.Canonicalize(remoteIpAddress);
        return EdgeGatewayIpAddress.IsLoopback(address) ||
               EdgeGatewayIpAddress.IsLanAddress(address);
    }
}
