using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class EdgeGatewayApplicationUpdateTests : IDisposable
{
    private readonly Guid applicationId = Guid.NewGuid();
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"lms-edge-app-update-{Guid.NewGuid():N}");

    [Fact]
    public async Task Update_application_refreshes_cloudflare_dns_and_tunnel_ingress()
    {
        Directory.CreateDirectory(tempRoot);
        var configurationStore = new InMemoryConfigurationStore(Configuration());
        var dnsService = new RecordingDnsService(
            new CloudflareDnsRecord(
                "old-record",
                "zone-id",
                "old.example.com",
                "CNAME",
                "old.ha-app-relay.example.com",
                true,
                1,
                "managed",
                null));
        var tunnelService = new RecordingTunnelService(
            new CloudflareTunnelConfiguration(
            [
                new CloudflareTunnelRoute("old.example.com", "http://localhost:18080"),
                new CloudflareTunnelRoute(string.Empty, "http_status:404")
            ]));
        var service = new EdgeGatewayRelayProvisioningService(
            CreateOptions(),
            new InMemoryTokenStore(),
            new StaticZoneService(),
            dnsService,
            tunnelService,
            configurationStore,
            new NeverRunningProcessProbe());

        var result = await service.UpdateApplicationAsync(
            applicationId,
            "Home Assistant",
            "new",
            "https",
            "192.168.15.3",
            8123,
            "Pass Through");

        Assert.True(result.Success);
        Assert.Contains("Configured tunnel ingress new.example.com -> http://localhost:18080.", result.Steps);
        Assert.Contains("Enabled Cloudflare tunnel originRequest noTLSVerify for HTTPS upstream https://192.168.15.3:8123.", result.Steps);
        Assert.Contains("Deleted stale route DNS record old.example.com -> old.ha-app-relay.example.com.", result.Steps);
        Assert.Contains("Removed stale tunnel ingress for old.example.com.", result.Steps);
        Assert.Contains(dnsService.Records, record =>
            record.Name.Equals("new.example.com", StringComparison.OrdinalIgnoreCase) &&
            record.Content.Equals("new.ha-app-relay.example.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dnsService.Records, record =>
            record.Name.Equals("old.example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("new.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.OriginRequest.NoTlsVerify);
        Assert.DoesNotContain(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("old.example.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Startup_refresh_reconciles_stale_cloudflare_tunnel_ingress_for_saved_apps()
    {
        Directory.CreateDirectory(tempRoot);
        var configuration = Configuration() with
        {
            Applications =
            [
                Configuration().Applications[0] with
                {
                    IsEnabled = true,
                    UpstreamUrl = "https://192.168.15.3:8123"
                }
            ],
            PublicProxyRoutes =
            [
                new PublicProxyRouteDefinition(
                    Guid.NewGuid(),
                    "oauth.example.com",
                    "/oauth",
                    "http://127.0.0.1:5055",
                    "Tesla Fleet Helper OAuth endpoints",
                    true)
            ]
        };
        var configurationStore = new InMemoryConfigurationStore(configuration);
        var tunnelService = new RecordingTunnelService(
            new CloudflareTunnelConfiguration(
            [
                new CloudflareTunnelRoute("old.example.com", "http://stale-origin:18080"),
                new CloudflareTunnelRoute("*.ha-app-relay.example.com", "http://stale-origin:18080"),
                new CloudflareTunnelRoute("external.example.com", "http://external-origin:8080"),
                new CloudflareTunnelRoute(string.Empty, "http_status:404")
            ]));
        var wellKnownStore = new StaticWellKnownServiceStore(new WellKnownConfiguration(
            [
                new WellKnownService(
                    Guid.NewGuid(),
                    "Tesla Fleet public key",
                    "tesla.example.com",
                    "/.well-known/appspecific/com.tesla.3p.public-key.pem",
                    "application/x-pem-file",
                    "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----",
                    WellKnownSourceType.Generated,
                    true,
                    false,
                    true,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
            ],
            DateTimeOffset.UtcNow));
        var service = new EdgeGatewayRelayProvisioningService(
            CreateOptions(),
            new InMemoryTokenStore(),
            new StaticZoneService(),
            new RecordingDnsService(),
            tunnelService,
            configurationStore,
            new NeverRunningProcessProbe(),
            wellKnownStore);

        var result = await service.RefreshPublishedConfigurationAsync();

        Assert.True(result.Success);
        Assert.Contains("Reconciled Cloudflare tunnel ingress for example.com", result.Summary);
        Assert.Contains("1 public proxy route(s)", result.Summary);
        Assert.Contains("Relay example.com validation:", result.Summary);
        Assert.Equal(1, tunnelService.UpdateCount);
        Assert.True(tunnelService.GetTunnelCount > 0);
        Assert.Contains(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("old.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.Service.Equals("http://localhost:18080", StringComparison.OrdinalIgnoreCase) &&
            route.OriginRequest.NoTlsVerify);
        Assert.Contains(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("*.ha-app-relay.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.Service.Equals("http://localhost:18080", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("tesla.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.Service.Equals("http://localhost:18080", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("oauth.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.Service.Equals("http://localhost:18080", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tunnelService.Configuration.Routes, route =>
            route.Hostname.Equals("external.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.Service.Equals("http://external-origin:8080", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, tunnelService.Configuration.Routes.Count(route => string.IsNullOrWhiteSpace(route.Hostname)));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private EdgeGatewayConfiguration Configuration() =>
        new(
            [
                new PublishedApplicationDefinition(
                    applicationId,
                    "Home Assistant",
                    "old.example.com",
                    "http://192.168.1.20:8123",
                    "Pass Through",
                    false)
            ],
            [
                new EdgeGatewayRelayZone(
                    "example.com",
                    "ha-app-relay.example.com",
                    DateTimeOffset.UtcNow,
                    "*.ha-app-relay.example.com",
                    "tunnel-id.cfargotunnel.com",
                    "tunnel-id",
                    "tunnel",
                    DateTimeOffset.UtcNow,
                    "healthy",
                    DateTimeOffset.UtcNow,
                    string.Empty)
            ],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);

    private IOptions<EdgeGatewayCoreOptions> CreateOptions() =>
        Options.Create(new EdgeGatewayCoreOptions
        {
            DataRoot = tempRoot,
            CaddyConfigPath = Path.Combine(tempRoot, "Caddyfile"),
            CaddyLocalServiceUrl = "http://localhost:18080",
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

    private sealed class InMemoryTokenStore : ICloudflareApiTokenStore
    {
        public Task<CloudflareApiTokenState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudflareApiTokenState(true, string.Empty));

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("api-token");

        public Task<string?> GetTunnelTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("connector-token");

        public Task SaveTokenAsync(string token, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearTunnelTokenAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearTokenAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StaticZoneService : ICloudflareZoneService
    {
        public Task<CloudflareZoneListResult> ListZonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudflareZoneListResult(
                [new CloudflareZoneSummary("zone-id", "example.com", "account-id", "account", "active", "full", [])],
                null));
    }

    private sealed class RecordingDnsService(params CloudflareDnsRecord[] records) : ICloudflareDnsService
    {
        public List<CloudflareDnsRecord> Records { get; } = [.. records];

        public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
            string apiToken,
            string zoneId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(Records.ToArray());

        public Task<CloudflareDnsRecord> CreateRecordAsync(
            string apiToken,
            string zoneId,
            CloudflareDnsRecord record,
            CancellationToken cancellationToken = default)
        {
            var created = record with { Id = $"{record.Name}-record", ZoneId = zoneId };
            Records.Add(created);
            return Task.FromResult(created);
        }

        public Task<CloudflareDnsRecord> UpdateRecordAsync(
            string apiToken,
            string zoneId,
            CloudflareDnsRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task DeleteRecordAsync(
            string apiToken,
            string zoneId,
            string recordId,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(record => record.Id.Equals(recordId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTunnelService(CloudflareTunnelConfiguration configuration) : ICloudflareTunnelService
    {
        public CloudflareTunnelConfiguration Configuration { get; private set; } = configuration;
        public int UpdateCount { get; private set; }
        public int GetTunnelCount { get; private set; }

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
            CancellationToken cancellationToken = default)
        {
            GetTunnelCount++;
            return Task.FromResult<CloudflareTunnel?>(new CloudflareTunnel(
                tunnelId,
                accountId,
                "tunnel",
                "cloudflare",
                "healthy",
                false,
                true,
                DateTimeOffset.UtcNow));
        }

        public Task<CloudflareTunnelConfiguration> GetConfigurationAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Configuration);

        public Task UpdateConfigurationAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CloudflareTunnelConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            Configuration = configuration;
            return Task.CompletedTask;
        }

        public Task<string> GetTunnelTokenAsync(
            string apiToken,
            string accountId,
            string tunnelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("connector-token");

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

    private sealed class StaticWellKnownServiceStore(WellKnownConfiguration configuration) : IWellKnownServiceStore
    {
        public Task<WellKnownConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(
            WellKnownConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NeverRunningProcessProbe : IProcessStatusProbe
    {
        public Task<bool> IsRunningAsync(string processPattern, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
