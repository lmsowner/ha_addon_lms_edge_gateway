using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class EdgeGatewayRelayProvisioningRepairTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"lms-edge-relay-repair-{Guid.NewGuid():N}");

    [Fact]
    public async Task Validate_relay_recovers_missing_cloudflared_connector_token()
    {
        Directory.CreateDirectory(tempRoot);
        var options = CreateOptions();
        var tokenStore = new JsonCloudflareApiTokenStore(options);
        await tokenStore.SaveTokenAsync("api-token");
        var configurationStore = new InMemoryConfigurationStore(Configuration());
        var tunnelService = new TokenRecoveringTunnelService();
        var service = new EdgeGatewayRelayProvisioningService(
            options,
            tokenStore,
            null!,
            null!,
            tunnelService,
            configurationStore,
            new NeverRunningProcessProbe());

        var result = await service.ValidateRelayAsync("example.com");

        Assert.Equal("connector-token", await tokenStore.GetTunnelTokenAsync());
        Assert.Contains("Recovered and saved the cloudflared connector token for this tunnel.", result.Steps);
        Assert.True(tunnelService.ConnectorTokenRequested);
    }

    [Fact]
    public async Task Repair_relay_refreshes_stale_cloudflared_connector_token_without_reset()
    {
        Directory.CreateDirectory(tempRoot);
        var options = CreateOptions();
        var tokenStore = new JsonCloudflareApiTokenStore(options);
        await tokenStore.SaveTokenAsync("api-token");
        await tokenStore.SaveTunnelTokenAsync("stale-token");
        var configurationStore = new InMemoryConfigurationStore(Configuration());
        var tunnelService = new TokenRecoveringTunnelService();
        var service = new EdgeGatewayRelayProvisioningService(
            options,
            tokenStore,
            null!,
            null!,
            tunnelService,
            configurationStore,
            new NeverRunningProcessProbe());

        var result = await service.RepairRelayAsync("example.com");

        Assert.Equal("connector-token", await tokenStore.GetTunnelTokenAsync());
        Assert.Contains("Fetched the current Cloudflare Tunnel connector token and saved it for cloudflared.", result.Steps);
        Assert.True(tunnelService.ConnectorTokenRequested);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static EdgeGatewayConfiguration Configuration() =>
        new(
            [],
            [
                new EdgeGatewayRelayZone(
                    "example.com",
                    "ha-app-relay.example.com",
                    DateTimeOffset.UtcNow,
                    "*.ha-app-relay.example.com",
                    "tunnel-id.cfargotunnel.com",
                    "tunnel-id",
                    "tunnel",
                    DateTimeOffset.UtcNow)
            ],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);

    private IOptions<EdgeGatewayCoreOptions> CreateOptions() =>
        Options.Create(new EdgeGatewayCoreOptions
        {
            DataRoot = tempRoot,
            CaddyConfigPath = Path.Combine(tempRoot, "Caddyfile"),
            CloudflaredExecutablePath = "/bin/false",
            OptionsJsonPath = Path.Combine(tempRoot, "options.json")
        });

    private sealed class InMemoryConfigurationStore(EdgeGatewayConfiguration configuration) : IEdgeGatewayConfigurationStore
    {
        private EdgeGatewayConfiguration current = configuration;

        public Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);

        public Task SaveAsync(
            EdgeGatewayConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            current = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class TokenRecoveringTunnelService : ICloudflareTunnelService
    {
        public bool ConnectorTokenRequested { get; private set; }

        public Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
            string apiToken,
            string accountId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudflareTunnel> CreateTunnelAsync(
            string apiToken,
            string accountId,
            string tunnelName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudflareTunnel?> GetTunnelAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudflareTunnel?>(new CloudflareTunnel(
                tunnelId,
                accountId,
                "tunnel",
                "cloudflare",
                "inactive",
                false,
                true,
                DateTimeOffset.UtcNow));

        public Task<CloudflareTunnelConfiguration> GetConfigurationAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateConfigurationAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CloudflareTunnelConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetTunnelTokenAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default)
        {
            ConnectorTokenRequested = true;
            return Task.FromResult("connector-token");
        }

        public Task DeleteTunnelAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteTunnelConnectionsAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NeverRunningProcessProbe : IProcessStatusProbe
    {
        public Task<bool> IsRunningAsync(string processPattern, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
