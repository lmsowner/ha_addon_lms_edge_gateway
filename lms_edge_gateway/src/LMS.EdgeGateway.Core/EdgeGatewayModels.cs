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
    bool IsEnabled,
    string TargetPathPrefix = "",
    string AllowKnownIps = "",
    string AllowedUsers = "",
    string AllowedGroups = "",
    bool AllowLanOnly = false,
    string Notes = "",
    bool? UsePublicHostHeader = null,
    bool? StripForwardedFor = null,
    bool? SkipUpstreamTlsVerification = null,
    string TemporaryIpApprovalRecipients = "",
    string TemporaryIpApprovalAllowedCountryCodes = "",
    bool TemporaryIpApprovalUseNotFoundResponse = false,
    int? TemporaryIpApprovalIdleTimeoutMinutes = null,
    int? TemporaryIpApprovalMaxLifetimeMinutes = null);

public sealed record PublicProxyRouteDefinition(
    Guid Id,
    string Hostname,
    string PathPrefix,
    string UpstreamUrl,
    string Description,
    bool Enabled,
    bool RequiresAuth = false,
    bool PreserveHostHeader = true,
    bool StripForwardedFor = true,
    bool MatchSubpaths = true,
    DateTimeOffset CreatedUtc = default,
    DateTimeOffset UpdatedUtc = default);

public sealed record CloudflareTunnelState(
    string TunnelName,
    string AccountName,
    string TunnelId,
    bool IsAuthenticated,
    DateTimeOffset? LastVerifiedAtUtc,
    string AccountId = "");

public sealed record EdgeGatewayRelayZone(
    string DomainName,
    string RelayHostname,
    DateTimeOffset CreatedAtUtc,
    string WildcardHostname = "",
    string DnsTarget = "",
    string TunnelId = "",
    string TunnelName = "",
    DateTimeOffset? ProvisionedAtUtc = null,
    string TunnelStatus = "",
    DateTimeOffset? LastValidatedAtUtc = null,
    string LastValidationError = "");

public sealed record EdgeGatewayConfiguration(
    IReadOnlyList<PublishedApplicationDefinition> Applications,
    IReadOnlyList<EdgeGatewayRelayZone> RelayZones,
    CloudflareTunnelState CloudflareTunnel,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PublicProxyRouteDefinition>? PublicProxyRoutes = null)
{
    public static EdgeGatewayConfiguration Empty { get; } = new(
        [],
        [],
        new CloudflareTunnelState(string.Empty, string.Empty, string.Empty, false, null, string.Empty),
        DateTimeOffset.UtcNow,
        []);
}

public sealed record EdgeGatewayRuntimeStatus(
    IReadOnlyList<EdgeGatewayComponentStatus> Components,
    EdgeGatewayConfiguration Configuration,
    bool IsHomeAssistantIngress,
    string IngressPath,
    string DataRoot,
    DateTimeOffset CheckedAtUtc);
