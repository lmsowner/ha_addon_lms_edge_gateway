using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayStatusService(
    IOptions<EdgeGatewayCoreOptions> options,
    IProcessStatusProbe processStatusProbe,
    IEdgeGatewayConfigurationStore configurationStore,
    ICloudflareApiTokenStore apiTokenStore) : IEdgeGatewayStatusService
{
    private const string CloudflaredProcessPattern = "(^|/)(cloudflared|lms-edge-cloudflared)( |$)";

    public async Task<EdgeGatewayRuntimeStatus> GetStatusAsync(
        string? ingressPath = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken);
        var apiTokenState = await apiTokenStore.GetStateAsync(cancellationToken);
        var tunnelToken = await apiTokenStore.GetTunnelTokenAsync(cancellationToken);
        var components = new List<EdgeGatewayComponentStatus>
        {
            await GetApplicationStatusAsync(cancellationToken),
            await GetCaddyStatusAsync(cancellationToken),
            await GetCloudflaredStatusAsync(configuration, apiTokenState, tunnelToken, cancellationToken),
            GetCloudflareStatus(configuration, apiTokenState)
        };

        return new EdgeGatewayRuntimeStatus(
            components,
            configuration,
            !string.IsNullOrWhiteSpace(ingressPath),
            ingressPath ?? string.Empty,
            GetDataRoot(),
            DateTimeOffset.UtcNow);
    }

    private static Task<EdgeGatewayComponentStatus> GetApplicationStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EdgeGatewayComponentStatus(
            "app",
            "Linux Made Sane - Edge Gateway",
            EdgeGatewayComponentState.Ready,
            "Control plane is running",
            "The Blazor orchestration layer is available through Home Assistant ingress or the configured port."));
    }

    private async Task<EdgeGatewayComponentStatus> GetCaddyStatusAsync(CancellationToken cancellationToken)
    {
        var configPath = ResolvePath(options.Value.CaddyConfigPath);
        var hasConfig = File.Exists(configPath);
        var isRunning = await processStatusProbe.IsRunningAsync("(^|/)caddy( |$)", cancellationToken);

        if (isRunning)
        {
            return new EdgeGatewayComponentStatus(
                "caddy",
                "Caddy",
                EdgeGatewayComponentState.Ready,
                "Reverse proxy is running",
                hasConfig ? configPath : "Caddy is running; default configuration will be created on next add-on start.");
        }

        return new EdgeGatewayComponentStatus(
            "caddy",
            "Caddy",
            hasConfig ? EdgeGatewayComponentState.Warning : EdgeGatewayComponentState.Starting,
            hasConfig ? "Caddy is not running" : "Waiting for Caddy configuration",
            hasConfig ? configPath : "The container init script creates /data/caddy/Caddyfile.");
    }

    private async Task<EdgeGatewayComponentStatus> GetCloudflaredStatusAsync(
        EdgeGatewayConfiguration configuration,
        CloudflareApiTokenState apiTokenState,
        string? tunnelToken,
        CancellationToken cancellationToken)
    {
        var healthyRelay = configuration.RelayZones.FirstOrDefault(relay =>
            !string.IsNullOrWhiteSpace(relay.TunnelId) &&
            IsTunnelHealthy(relay.TunnelStatus));
        if (healthyRelay is not null)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflared",
                "Cloudflare Tunnel",
                EdgeGatewayComponentState.Ready,
                "Tunnel connector is online",
                $"{healthyRelay.DomainName}: Cloudflare reports tunnel {healthyRelay.TunnelName} as healthy.");
        }

        var isRunning = await processStatusProbe.IsRunningAsync(CloudflaredProcessPattern, cancellationToken);

        if (isRunning)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflared",
                "Cloudflare Tunnel",
                EdgeGatewayComponentState.Ready,
                "Tunnel process is running",
                "cloudflared is active inside the add-on container.");
        }

        if (!string.IsNullOrWhiteSpace(tunnelToken))
        {
            return new EdgeGatewayComponentStatus(
                "cloudflared",
                "Cloudflare Tunnel",
                EdgeGatewayComponentState.Starting,
                "Tunnel token saved",
                "cloudflared has a tunnel token saved. Restart the add-on if the tunnel process does not start automatically.");
        }

        return new EdgeGatewayComponentStatus(
            "cloudflared",
            "Cloudflare Tunnel",
            apiTokenState.HasToken ? EdgeGatewayComponentState.Starting : EdgeGatewayComponentState.Disabled,
            apiTokenState.HasToken ? "Tunnel not provisioned yet" : "Cloudflare API token required",
            apiTokenState.HasToken
                ? "The API token is ready; tunnel provisioning is the next setup step."
                : "Add and validate a Cloudflare API token before provisioning a tunnel.");
    }

    private static EdgeGatewayComponentStatus GetCloudflareStatus(
        EdgeGatewayConfiguration configuration,
        CloudflareApiTokenState apiTokenState)
    {
        var provisionedRelays = configuration.RelayZones
            .Where(relay => !string.IsNullOrWhiteSpace(relay.TunnelId) &&
                            !string.IsNullOrWhiteSpace(relay.DnsTarget) &&
                            !string.IsNullOrWhiteSpace(relay.WildcardHostname))
            .ToArray();
        var healthyRelays = provisionedRelays
            .Where(relay => IsTunnelHealthy(relay.TunnelStatus))
            .ToArray();

        if (healthyRelays.Length > 0)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflare",
                "Cloudflare",
                EdgeGatewayComponentState.Ready,
                "Cloudflare tunnel healthy",
                string.Join(", ", healthyRelays.Select(relay => $"{relay.DomainName}: {NormalizeTunnelStatus(relay.TunnelStatus)}")));
        }

        if (provisionedRelays.Length > 0)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflare",
                "Cloudflare",
                EdgeGatewayComponentState.Warning,
                "Cloudflare tunnel not healthy",
                string.Join(", ", provisionedRelays.Select(relay =>
                    $"{relay.DomainName}: {NormalizeTunnelStatus(relay.TunnelStatus)}")));
        }

        if (apiTokenState.HasToken)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflare",
                "Cloudflare",
                EdgeGatewayComponentState.Warning,
                "API token saved; relay not provisioned",
                "Run Setup Relay to create the Cloudflare tunnel, wildcard DNS, tunnel ingress, cloudflared token, and Caddy config.");
        }

        return new EdgeGatewayComponentStatus(
            "cloudflare",
            "Cloudflare",
            EdgeGatewayComponentState.Disabled,
            "Cloudflare not connected",
            "Save a Cloudflare API token before provisioning the relay.");
    }

    private static bool IsTunnelHealthy(string status) =>
        NormalizeTunnelStatus(status).Equals("healthy", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTunnelStatus(string status) =>
        string.IsNullOrWhiteSpace(status)
            ? "unknown"
            : status.Trim().ToLowerInvariant();

    private string GetDataRoot() => ResolvePath(options.Value.DataRoot);

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}
