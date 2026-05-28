namespace LMS.EdgeGateway.Core;

public enum EdgeGatewayComponentState
{
    Unknown,
    Disabled,
    Starting,
    Ready,
    Warning,
    Error
}

public sealed record EdgeGatewayComponentStatus(
    string Id,
    string Name,
    EdgeGatewayComponentState State,
    string Summary,
    string Detail);

public sealed record PublishedApplicationDefinition(
    Guid Id,
    string Name,
    string PublicHostname,
    string UpstreamUrl,
    string AccessPolicy,
    bool IsEnabled);

public sealed record CloudflareTunnelState(
    string TunnelName,
    string AccountName,
    string TunnelId,
    bool IsAuthenticated,
    DateTimeOffset? LastVerifiedAtUtc);

public sealed record EdgeGatewayConfiguration(
    IReadOnlyList<PublishedApplicationDefinition> Applications,
    CloudflareTunnelState CloudflareTunnel,
    DateTimeOffset UpdatedAtUtc)
{
    public static EdgeGatewayConfiguration Empty { get; } = new(
        [],
        new CloudflareTunnelState(string.Empty, string.Empty, string.Empty, false, null),
        DateTimeOffset.UtcNow);
}

public sealed record EdgeGatewayRuntimeStatus(
    IReadOnlyList<EdgeGatewayComponentStatus> Components,
    EdgeGatewayConfiguration Configuration,
    bool IsHomeAssistantIngress,
    string IngressPath,
    string DataRoot,
    DateTimeOffset CheckedAtUtc);
