namespace LMS.EdgeGateway.Core;

public sealed record CloudflareApiError(
    int Code,
    string Message,
    string? DocumentationUrl,
    string? Pointer);

public sealed class CloudflareApiException(
    int statusCode,
    IReadOnlyList<CloudflareApiError> errors,
    Exception? innerException = null)
    : Exception(BuildMessage(statusCode, errors), innerException)
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyList<CloudflareApiError> Errors { get; } = errors;

    private static string BuildMessage(int statusCode, IReadOnlyList<CloudflareApiError> errors)
    {
        var message = string.Join(" ", errors
            .Select(error => error.Message)
            .Where(error => !string.IsNullOrWhiteSpace(error)));

        return string.IsNullOrWhiteSpace(message)
            ? $"Cloudflare API request failed with HTTP {statusCode}."
            : message;
    }
}

public sealed record CloudflareDnsRecord(
    string Id,
    string ZoneId,
    string Name,
    string Type,
    string Content,
    bool Proxied,
    int Ttl,
    string Comment,
    DateTimeOffset? ModifiedAtUtc);

public sealed record CloudflareTunnel(
    string Id,
    string AccountId,
    string Name,
    string ConfigSource,
    string Status,
    bool IsDeleted,
    bool IsManagedByLinuxMadeSane,
    DateTimeOffset CreatedAtUtc);

public sealed record CloudflareTunnelConfiguration(
    IReadOnlyList<CloudflareTunnelRoute> Routes);

public sealed record CloudflareTunnelRoute
{
    public CloudflareTunnelRoute(string hostname, string service)
        : this(hostname, service, CloudflareOriginRequestSettings.Default)
    {
    }

    public CloudflareTunnelRoute(string hostname, string service, bool noTlsVerify)
        : this(hostname, service, CloudflareOriginRequestSettings.Default with { NoTlsVerify = noTlsVerify })
    {
    }

    public CloudflareTunnelRoute(
        string hostname,
        string service,
        CloudflareOriginRequestSettings? originRequest)
    {
        Hostname = hostname;
        Service = service;
        OriginRequest = originRequest ?? CloudflareOriginRequestSettings.Default;
    }

    public string Hostname { get; init; }
    public string Service { get; init; }
    public CloudflareOriginRequestSettings OriginRequest { get; init; }
}

public sealed record CloudflareOriginRequestSettings(
    string OriginServerName = "",
    string CertificateAuthorityPool = "",
    bool NoTlsVerify = false,
    int TlsTimeoutSeconds = 10,
    bool Http2Origin = false,
    bool MatchSniToHost = false,
    string HttpHostHeader = "",
    bool DisableChunkedEncoding = false,
    int ConnectTimeoutSeconds = 30,
    bool NoHappyEyeballs = false,
    string ProxyType = "",
    int KeepAliveTimeoutSeconds = 90,
    int KeepAliveConnections = 100,
    int TcpKeepAliveSeconds = 30)
{
    public static CloudflareOriginRequestSettings Default { get; } = new();
}

public sealed record EdgeGatewayRelayProvisioningResult(
    bool Success,
    bool RequiresDnsReplacement,
    EdgeGatewayRelayZone? Relay,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

public sealed record EdgeGatewayRelayRemovalResult(
    bool Success,
    string DomainName,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

public sealed record EdgeGatewayRelayValidationResult(
    bool Success,
    EdgeGatewayRelayZone? Relay,
    string Summary,
    string TunnelStatus,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

public sealed record EdgeGatewayApplicationSaveResult(
    bool Success,
    PublishedApplicationDefinition? Application,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

public sealed record EdgeGatewayApplicationTestResult(
    bool Success,
    Guid ApplicationId,
    string Summary,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Warnings);

public sealed record EdgeGatewayCaddyConfigurationResult(
    bool Success,
    string Summary,
    IReadOnlyList<string> Warnings);
