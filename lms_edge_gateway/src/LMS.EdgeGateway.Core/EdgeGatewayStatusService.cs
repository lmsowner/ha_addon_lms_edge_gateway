using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayStatusService(
    IOptions<EdgeGatewayCoreOptions> options,
    IProcessStatusProbe processStatusProbe,
    IEdgeGatewayConfigurationStore configurationStore,
    ICloudflareApiTokenStore apiTokenStore) : IEdgeGatewayStatusService
{
    public async Task<EdgeGatewayRuntimeStatus> GetStatusAsync(
        string? ingressPath = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken);
        var apiTokenState = await apiTokenStore.GetStateAsync(cancellationToken);
        var components = new List<EdgeGatewayComponentStatus>
        {
            await GetApplicationStatusAsync(cancellationToken),
            await GetCaddyStatusAsync(cancellationToken),
            await GetCloudflaredStatusAsync(apiTokenState, cancellationToken),
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
            "LMS Edge Gateway",
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
        CloudflareApiTokenState apiTokenState,
        CancellationToken cancellationToken)
    {
        var isRunning = await processStatusProbe.IsRunningAsync("(^|/)cloudflared( |$)", cancellationToken);

        if (isRunning)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflared",
                "Cloudflare Tunnel",
                EdgeGatewayComponentState.Ready,
                "Tunnel process is running",
                "cloudflared is active inside the add-on container.");
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
        if (apiTokenState.HasToken)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflare",
                "Cloudflare",
                EdgeGatewayComponentState.Ready,
                "API token validated",
                "Cloudflare API credentials are stored for tunnel, Access, and DNS automation.");
        }

        if (configuration.CloudflareTunnel.IsAuthenticated)
        {
            return new EdgeGatewayComponentStatus(
                "cloudflare",
                "Cloudflare",
                EdgeGatewayComponentState.Ready,
                "Cloudflare account connected",
                string.IsNullOrWhiteSpace(configuration.CloudflareTunnel.AccountName)
                    ? "Cloudflare API credentials are stored."
                    : configuration.CloudflareTunnel.AccountName);
        }

        return new EdgeGatewayComponentStatus(
            "cloudflare",
            "Cloudflare",
            EdgeGatewayComponentState.Disabled,
            "Cloudflare not connected",
            "Connect a Cloudflare account in Phase 2 to automate DNS, Access, and tunnel lifecycle.");
    }

    private string GetDataRoot() => ResolvePath(options.Value.DataRoot);

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}
