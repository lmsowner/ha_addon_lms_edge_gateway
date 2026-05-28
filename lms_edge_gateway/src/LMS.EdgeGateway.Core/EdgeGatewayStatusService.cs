using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayStatusService(
    IOptions<EdgeGatewayCoreOptions> options,
    IProcessStatusProbe processStatusProbe,
    IEdgeGatewayConfigurationStore configurationStore) : IEdgeGatewayStatusService
{
    public async Task<EdgeGatewayRuntimeStatus> GetStatusAsync(
        string? ingressPath = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken);
        var components = new List<EdgeGatewayComponentStatus>
        {
            await GetApplicationStatusAsync(cancellationToken),
            await GetCaddyStatusAsync(cancellationToken),
            await GetCloudflaredStatusAsync(cancellationToken),
            GetCloudflareStatus(configuration)
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

    private async Task<EdgeGatewayComponentStatus> GetCloudflaredStatusAsync(CancellationToken cancellationToken)
    {
        var hasTunnelToken = await HasCloudflareTunnelTokenAsync(cancellationToken);
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
            hasTunnelToken ? EdgeGatewayComponentState.Warning : EdgeGatewayComponentState.Disabled,
            hasTunnelToken ? "Tunnel token configured but process is not running" : "Tunnel not configured",
            hasTunnelToken
                ? "Check the add-on log for cloudflared startup output."
                : "Phase 2 will provision named tunnels through the Cloudflare API. A manual token can already be supplied in options.json for testing.");
    }

    private static EdgeGatewayComponentStatus GetCloudflareStatus(EdgeGatewayConfiguration configuration)
    {
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

    private async Task<bool> HasCloudflareTunnelTokenAsync(CancellationToken cancellationToken)
    {
        var optionsPath = ResolvePath(options.Value.OptionsJsonPath);
        if (!File.Exists(optionsPath))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(optionsPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("cloudflare_tunnel_token", out var token) &&
                token.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(token.GetString());
        }
        catch
        {
            return false;
        }
    }

    private string GetDataRoot() => ResolvePath(options.Value.DataRoot);

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}
