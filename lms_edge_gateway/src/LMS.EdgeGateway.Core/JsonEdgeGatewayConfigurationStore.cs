using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonEdgeGatewayConfigurationStore(IOptions<EdgeGatewayCoreOptions> options) : IEdgeGatewayConfigurationStore
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        if (!File.Exists(path))
        {
            return EdgeGatewayConfiguration.Empty;
        }

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<EdgeGatewayConfiguration>(stream, jsonOptions, cancellationToken);
        return Normalize(configuration);
    }

    public async Task SaveAsync(EdgeGatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.Value.DataRoot);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, configuration with { UpdatedAtUtc = DateTimeOffset.UtcNow }, jsonOptions, cancellationToken);
    }

    private string GetConfigurationPath()
    {
        var dataRoot = options.Value.DataRoot;
        var root = Path.IsPathRooted(dataRoot)
            ? dataRoot
            : Path.GetFullPath(dataRoot);

        return Path.Combine(root, "edge-gateway.json");
    }

    private static EdgeGatewayConfiguration Normalize(EdgeGatewayConfiguration? configuration)
    {
        if (configuration is null)
        {
            return EdgeGatewayConfiguration.Empty;
        }

        return configuration with
        {
            Applications = NormalizeApplications(configuration.Applications),
            PublicProxyRoutes = NormalizePublicProxyRoutes(configuration.PublicProxyRoutes),
            RelayZones = NormalizeRelayZones(configuration.RelayZones),
            CloudflareTunnel = NormalizeTunnel(configuration.CloudflareTunnel)
        };
    }

    private static IReadOnlyList<PublishedApplicationDefinition> NormalizeApplications(
        IReadOnlyList<PublishedApplicationDefinition>? applications) =>
        applications?
            .Where(application => application.Id != Guid.Empty &&
                                  !string.IsNullOrWhiteSpace(application.PublicHostname) &&
                                  !string.IsNullOrWhiteSpace(application.UpstreamUrl))
            .Select(application => application with
            {
                Name = application.Name?.Trim() ?? string.Empty,
                PublicHostname = application.PublicHostname?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty,
                UpstreamUrl = application.UpstreamUrl?.Trim() ?? string.Empty,
                AccessPolicy = string.IsNullOrWhiteSpace(application.AccessPolicy)
                    ? EdgeGatewayAccessPolicies.MfaPasskey
                    : application.AccessPolicy.Trim(),
                TargetPathPrefix = application.TargetPathPrefix?.Trim() ?? string.Empty,
                AllowKnownIps = application.AllowKnownIps?.Trim() ?? string.Empty,
                AllowedUsers = application.AllowedUsers?.Trim() ?? string.Empty,
                AllowedGroups = application.AllowedGroups?.Trim() ?? string.Empty,
                Notes = application.Notes?.Trim() ?? string.Empty,
                TemporaryIpApprovalRecipients = application.TemporaryIpApprovalRecipients?.Trim() ?? string.Empty,
                TemporaryIpApprovalAllowedCountryCodes = NormalizeCountryCodeList(application.TemporaryIpApprovalAllowedCountryCodes),
                TemporaryIpApprovalUseNotFoundResponse = application.TemporaryIpApprovalUseNotFoundResponse
            })
            .ToArray() ?? [];

    private static CloudflareTunnelState NormalizeTunnel(CloudflareTunnelState? tunnel) =>
        tunnel is null
            ? new CloudflareTunnelState(string.Empty, string.Empty, string.Empty, false, null, string.Empty)
            : tunnel with
            {
                TunnelName = tunnel.TunnelName ?? string.Empty,
                AccountName = tunnel.AccountName ?? string.Empty,
                TunnelId = tunnel.TunnelId ?? string.Empty,
                AccountId = tunnel.AccountId ?? string.Empty
            };

    private static IReadOnlyList<EdgeGatewayRelayZone> NormalizeRelayZones(
        IReadOnlyList<EdgeGatewayRelayZone>? relayZones) =>
        relayZones?
            .Select(relay => relay with
            {
                DomainName = relay.DomainName ?? string.Empty,
                RelayHostname = relay.RelayHostname ?? string.Empty,
                WildcardHostname = relay.WildcardHostname ?? string.Empty,
                DnsTarget = relay.DnsTarget ?? string.Empty,
                TunnelId = relay.TunnelId ?? string.Empty,
                TunnelName = relay.TunnelName ?? string.Empty
            })
            .Where(relay => !string.IsNullOrWhiteSpace(relay.DomainName))
            .ToArray() ?? [];

    private static IReadOnlyList<PublicProxyRouteDefinition> NormalizePublicProxyRoutes(
        IReadOnlyList<PublicProxyRouteDefinition>? routes) =>
        routes?
            .Select(route => route with
            {
                Hostname = route.Hostname?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty,
                PathPrefix = string.IsNullOrWhiteSpace(route.PathPrefix) ? "/" : route.PathPrefix.Trim(),
                UpstreamUrl = route.UpstreamUrl?.Trim() ?? string.Empty,
                Description = route.Description?.Trim() ?? string.Empty,
                CreatedUtc = route.CreatedUtc == default ? DateTimeOffset.UtcNow : route.CreatedUtc,
                UpdatedUtc = route.UpdatedUtc == default ? DateTimeOffset.UtcNow : route.UpdatedUtc
            })
            .Where(route =>
                route.Id != Guid.Empty &&
                !string.IsNullOrWhiteSpace(route.Hostname) &&
                !string.IsNullOrWhiteSpace(route.PathPrefix) &&
                !string.IsNullOrWhiteSpace(route.UpstreamUrl))
            .ToArray() ?? [];

    private static string NormalizeCountryCodeList(string? value) =>
        string.Join(
            ", ",
            (value ?? string.Empty)
            .Split([',', '\r', '\n', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(country => country.ToUpperInvariant())
            .Where(country => country.Length == 2 && country.All(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(country => country, StringComparer.OrdinalIgnoreCase));
}
