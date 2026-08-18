using System.Text.Json.Serialization;

namespace LMS.EdgeGateway.Core;

public enum DiscoveryExposure
{
    Publishable,
    InternalOnly,
    RequiresManualConfirmation,
    UnsafeToExpose
}

public sealed record LocalHttpServiceEndpoint(
    string Url,
    string Scheme,
    string Host,
    int Port,
    int StatusCode,
    string? Title,
    string? ServerHeader,
    string Scope = "Localhost",
    string? IpAddress = null,
    string? DisplayName = null,
    DateTimeOffset? DiscoveredAtUtc = null,
    int Confidence = 0,
    string ServiceName = "",
    string ServiceKind = "unknown-http",
    DiscoveryExposure Exposure = DiscoveryExposure.RequiresManualConfirmation,
    string Fingerprint = "",
    IReadOnlyList<string>? Evidence = null);

public sealed record LocalHttpServiceDiscoveryRequest(
    bool IncludeLocalhost = true,
    bool IncludeLan = true,
    bool IncludeTailnet = false,
    bool IncludeDocker = false);

public sealed record LocalHttpServiceDiscoveryProgressUpdate(
    string Message,
    int ProbedCount,
    int TotalProbeCount,
    int FoundCount,
    LocalHttpServiceEndpoint? FoundEndpoint = null,
    bool IsCompleted = false);

[JsonSerializable(typeof(LocalHttpServiceEndpoint[]))]
internal sealed partial class LocalHttpServiceDiscoveryJsonContext : JsonSerializerContext;
