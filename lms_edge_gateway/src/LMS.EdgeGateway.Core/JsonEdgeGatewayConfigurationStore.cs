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
            Applications = configuration.Applications ?? [],
            PublicProxyRoutes = NormalizePublicProxyRoutes(configuration.PublicProxyRoutes),
            RelayZones = NormalizeRelayZones(configuration.RelayZones),
            CloudflareTunnel = NormalizeTunnel(configuration.CloudflareTunnel)
        };
    }

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
}
