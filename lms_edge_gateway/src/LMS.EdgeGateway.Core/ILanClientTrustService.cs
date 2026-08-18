using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LMS.EdgeGateway.Core;

public interface ILanClientTrustService
{
    Task<LanClientTrustResult> EvaluateAsync(
        PublishedApplicationDefinition route,
        string sourceIp,
        string cloudflareConnectingIp,
        CancellationToken cancellationToken = default);
}

public sealed record LanClientTrustResult(
    bool IsTrusted,
    string Reason,
    string HostName = "",
    int? LatencyMilliseconds = null);

public interface IDnsNameResolver
{
    Task<string?> ResolvePtrAsync(IPAddress address, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IPAddress>> ResolveForwardAsync(string hostName, CancellationToken cancellationToken = default);
}

public interface ILanLatencyProbe
{
    Task<int?> MeasureMillisecondsAsync(IPAddress address, int timeoutMilliseconds, CancellationToken cancellationToken = default);
}
