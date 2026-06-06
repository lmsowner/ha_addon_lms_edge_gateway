using System.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class WellKnownServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"lms-edge-well-known-{Guid.NewGuid():N}");

    [Fact]
    public void Path_normalization_accepts_only_well_known_paths()
    {
        Assert.Equal("/.well-known/security.txt", WellKnownPath.NormalizeRelativePath("security.txt"));
        Assert.Equal("/.well-known/webfinger", WellKnownPath.NormalizeRelativePath("/.well-known/webfinger"));
        Assert.Throws<ArgumentException>(() => WellKnownPath.NormalizeRelativePath("/admin/config.json"));
        Assert.Throws<ArgumentException>(() => WellKnownPath.NormalizeRelativePath("/.well-known/../secret.txt"));
        Assert.Throws<ArgumentException>(() => WellKnownPath.NormalizeRelativePath("C:\\secret.txt"));
    }

    [Fact]
    public void Content_type_mapping_defaults_json_and_text()
    {
        Assert.Equal("application/json", WellKnownPath.GuessContentType("/.well-known/assetlinks.json", WellKnownSourceType.StaticText));
        Assert.Equal("application/jrd+json", WellKnownPath.GuessContentType("/.well-known/webfinger", WellKnownSourceType.StaticText));
        Assert.Equal("application/x-pem-file", WellKnownPath.GuessContentType("/.well-known/appspecific/com.tesla.3p.public-key.pem", WellKnownSourceType.Generated));
        Assert.Equal("text/plain; charset=utf-8", WellKnownPath.GuessContentType("/.well-known/security.txt", WellKnownSourceType.StaticText));
    }

    [Fact]
    public async Task Tesla_template_publishes_public_key_and_keeps_private_key_out_of_public_store()
    {
        Directory.CreateDirectory(tempRoot);
        var manager = CreateManager(new StaticResponseHandler(HttpStatusCode.OK, "application/x-pem-file", "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----"));

        var result = await manager.CreateTeslaFleetAsync("example.com");

        Assert.True(result.Success);
        Assert.NotNull(result.Service);
        var service = result.Service!;
        Assert.Equal("/.well-known/appspecific/com.tesla.3p.public-key.pem", service.RelativePath);
        Assert.Contains("-----BEGIN PUBLIC KEY-----", service.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", service.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(service.PublicFilePath));
        Assert.True(File.Exists(service.SecretFilePath));
        Assert.Contains("PRIVATE KEY", await File.ReadAllTextAsync(service.SecretFilePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secrets", service.PublicFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_enable_disable_updates_store_and_refreshes_caddy()
    {
        Directory.CreateDirectory(tempRoot);
        var relay = new RecordingRelayProvisioningService();
        var manager = CreateManager(new StaticResponseHandler(HttpStatusCode.OK, "text/plain", "ok"), relay);

        var save = await manager.SaveAsync(new WellKnownServiceSaveRequest(
            null,
            "Security",
            "example.com",
            "/.well-known/security.txt",
            "text/plain; charset=utf-8",
            "Contact: mailto:security@example.com\nExpires: 2027-01-01T00:00:00Z\n",
            WellKnownSourceType.StaticText));

        Assert.True(save.Success);
        Assert.NotNull(save.Service);
        var service = save.Service!;

        var disabled = await manager.SetEnabledAsync(service.Id, false);

        Assert.True(disabled.Success);
        Assert.NotNull(disabled.Service);
        Assert.False(disabled.Service!.Enabled);
        Assert.True(relay.RefreshCount >= 2);
    }

    [Fact]
    public async Task Save_updates_existing_service_content_type_without_creating_duplicate()
    {
        Directory.CreateDirectory(tempRoot);
        var manager = CreateManager(new StaticResponseHandler(HttpStatusCode.OK, "text/plain", "ok"));
        var save = await manager.SaveAsync(new WellKnownServiceSaveRequest(
            null,
            "Tesla public key",
            "tesla.example.com",
            "/.well-known/appspecific/com.tesla.3p.public-key.pem",
            "text/plain; charset=utf-8",
            "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----",
            WellKnownSourceType.Generated,
            Template: WellKnownTemplateKind.TeslaFleet.ToString()));
        Assert.True(save.Success);
        Assert.NotNull(save.Service);

        var update = await manager.SaveAsync(new WellKnownServiceSaveRequest(
            save.Service!.Id,
            save.Service.DisplayName,
            save.Service.Domain,
            save.Service.RelativePath,
            "application/x-pem-file",
            save.Service.Body,
            save.Service.SourceType,
            save.Service.Enabled,
            save.Service.RequiresAuth,
            save.Service.PublicReadOnly,
            save.Service.CacheControl,
            save.Service.Template));

        var configuration = await manager.GetConfigurationAsync();
        Assert.True(update.Success);
        Assert.Single(configuration.Services);
        Assert.Equal("application/x-pem-file", configuration.Services[0].ContentType);
    }

    [Fact]
    public async Task Save_rejects_private_key_material_in_public_body_without_confirmation()
    {
        Directory.CreateDirectory(tempRoot);
        var manager = CreateManager(new StaticResponseHandler(HttpStatusCode.OK, "text/plain", "ok"));

        var result = await manager.SaveAsync(new WellKnownServiceSaveRequest(
            null,
            "Bad key",
            "example.com",
            "/.well-known/bad-key.pem",
            "application/x-pem-file",
            "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----",
            WellKnownSourceType.StaticText));

        Assert.False(result.Success);
        Assert.Contains("private key material", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_configures_cloudflare_dns_and_tunnel_ingress_for_enabled_service()
    {
        Directory.CreateDirectory(tempRoot);
        var options = Options.Create(new EdgeGatewayCoreOptions
        {
            DataRoot = tempRoot,
            CaddyConfigPath = Path.Combine(tempRoot, "Caddyfile"),
            CaddyLocalServiceUrl = "http://localhost:18080",
            ManagedRecordComment = "Managed by tests"
        });
        var edgeConfiguration = new EdgeGatewayConfiguration(
            [],
            [
                new EdgeGatewayRelayZone(
                    "example.com",
                    "ha-app-relay.example.com",
                    DateTimeOffset.UtcNow,
                    "*.ha-app-relay.example.com",
                    "tunnel.cfargotunnel.com",
                    "tunnel-id",
                    "tunnel",
                    DateTimeOffset.UtcNow,
                    "healthy")
            ],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);
        var dns = new RecordingDnsService();
        var tunnel = new RecordingTunnelService();
        var manager = new WellKnownServiceManager(
            options,
            new JsonWellKnownServiceStore(options),
            new RecordingRelayProvisioningService(),
            new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, "text/plain", "ok")),
            new InMemoryEdgeGatewayConfigurationStore(edgeConfiguration),
            new InMemoryTokenStore(),
            new StaticZoneService(),
            dns,
            tunnel);

        var result = await manager.SaveAsync(new WellKnownServiceSaveRequest(
            null,
            "Tesla public key",
            "tesla.example.com",
            "/.well-known/appspecific/com.tesla.3p.public-key.pem",
            "application/x-pem-file",
            "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----",
            WellKnownSourceType.Generated,
            Template: WellKnownTemplateKind.TeslaFleet.ToString()));

        Assert.True(result.Success);
        Assert.Contains(dns.Records, record =>
            record.Name.Equals("tesla.example.com", StringComparison.OrdinalIgnoreCase) &&
            record.Content.Equals("tesla.ha-app-relay.example.com", StringComparison.OrdinalIgnoreCase) &&
            record.Proxied);
        Assert.Contains(tunnel.Configuration.Routes, route =>
            route.Hostname.Equals("tesla.example.com", StringComparison.OrdinalIgnoreCase) &&
            route.Service.Equals("http://localhost:18080", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Caddy_route_renderer_serves_well_known_before_auth_routes()
    {
        var service = Service("example.com", "/.well-known/security.txt", "text/plain; charset=utf-8", "security");
        var caddy = WellKnownCaddyRouteRenderer.Render([service], tempRoot, "127.0.0.1:5299");

        Assert.Contains("@well_known_", caddy, StringComparison.Ordinal);
        Assert.Contains("host example.com", caddy, StringComparison.Ordinal);
        Assert.Contains("path /.well-known/security.txt", caddy, StringComparison.Ordinal);
        Assert.Contains("header Content-Type \"text/plain; charset=utf-8\"", caddy, StringComparison.Ordinal);
        Assert.Contains($"rewrite * /edge-well-known/{service.Id:N}", caddy, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy 127.0.0.1:5299", caddy, StringComparison.Ordinal);
        Assert.Contains("header_up X-LMS-Well-Known-Proxy 1", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("file_server", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("forward_auth", caddy, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_proxy_route_renderer_bypasses_auth_and_preserves_oauth_path()
    {
        var route = new PublicProxyRouteDefinition(
            Guid.NewGuid(),
            "tesla.example.com",
            "/oauth",
            "http://127.0.0.1:5055",
            "Tesla Fleet Helper OAuth endpoints",
            true,
            RequiresAuth: false);

        var caddy = PublicProxyRouteCaddyRenderer.Render([route], "127.0.0.1:5299");

        Assert.Contains("@public_proxy_", caddy, StringComparison.Ordinal);
        Assert.Contains("host tesla.example.com", caddy, StringComparison.Ordinal);
        Assert.Contains("path /oauth /oauth/*", caddy, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy http://127.0.0.1:5055", caddy, StringComparison.Ordinal);
        Assert.Contains("header_up X-Forwarded-Host {host}", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("forward_auth", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("uri strip_prefix", caddy, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_includes_well_known_bypass_before_protected_app_route()
    {
        var relay = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                DataRoot = tempRoot,
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var method = typeof(EdgeGatewayRelayProvisioningService)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "GenerateCaddyfileWithWellKnown" && method.GetParameters().Length == 2);
        var configuration = new EdgeGatewayConfiguration(
            [
                new PublishedApplicationDefinition(
                    Guid.NewGuid(),
                    "Protected App",
                    "example.com",
                    "http://192.168.1.20:8080",
                    "MFA/Passkey",
                    true)
            ],
            [],
            new CloudflareTunnelState(string.Empty, string.Empty, string.Empty, false, null),
            DateTimeOffset.UtcNow);
        var wellKnown = Service("example.com", "/.well-known/security.txt", "text/plain; charset=utf-8", "security");

        var caddy = Assert.IsType<string>(method.Invoke(relay, [configuration, new[] { wellKnown }]));

        Assert.True(caddy.IndexOf(".well-known service", StringComparison.Ordinal) < caddy.IndexOf("forward_auth", StringComparison.Ordinal));
        Assert.Contains("path /.well-known/security.txt", caddy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verification_accepts_tesla_public_key_without_following_auth_redirects()
    {
        Directory.CreateDirectory(tempRoot);
        var manager = CreateManager(new StaticResponseHandler(
            HttpStatusCode.OK,
            "application/x-pem-file",
            "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----"));
        var save = await manager.SaveAsync(new WellKnownServiceSaveRequest(
            null,
            "Tesla Fleet public key",
            "example.com",
            "/.well-known/appspecific/com.tesla.3p.public-key.pem",
            "application/x-pem-file",
            "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----",
            WellKnownSourceType.Generated,
            Template: WellKnownTemplateKind.TeslaFleet.ToString()));
        Assert.NotNull(save.Service);
        var service = save.Service!;

        var verification = await manager.VerifyAsync(service.Id);

        Assert.True(verification.Success);
        Assert.Equal("Verified", verification.Status);
        Assert.Contains(verification.Checks, check => check.Contains("HTTP 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configured_public_root_serves_well_known_files_without_exposing_tesla_private_key()
    {
        var dataRoot = Path.Combine(tempRoot, "data");
        var publicRoot = Path.Combine(tempRoot, "share", "lms-edge-gateway", "well-known", "public");
        var options = Options.Create(new EdgeGatewayCoreOptions
        {
            DataRoot = dataRoot,
            WellKnownPublicRoot = publicRoot,
            CaddyConfigPath = Path.Combine(tempRoot, "Caddyfile")
        });
        var manager = new WellKnownServiceManager(
            options,
            new JsonWellKnownServiceStore(options),
            new RecordingRelayProvisioningService(),
            new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, "application/x-pem-file", "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----")));

        var result = await manager.CreateTeslaFleetAsync("tesla.example.com");

        Assert.True(result.Success);
        Assert.NotNull(result.Service);
        var service = result.Service!;
        Assert.StartsWith(publicRoot, service.PublicFilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(service.PublicFilePath));
        Assert.Contains("-----BEGIN PUBLIC KEY-----", await File.ReadAllTextAsync(service.PublicFilePath), StringComparison.Ordinal);
        Assert.StartsWith(Path.Combine(dataRoot, "secrets"), service.SecretFilePath, StringComparison.Ordinal);
        Assert.False(service.SecretFilePath.StartsWith(publicRoot, StringComparison.Ordinal));
        Assert.Contains("PRIVATE KEY", await File.ReadAllTextAsync(service.SecretFilePath), StringComparison.OrdinalIgnoreCase);

        var caddy = WellKnownCaddyRouteRenderer.Render([service], dataRoot, publicRoot, "127.0.0.1:5299");
        Assert.Contains($"rewrite * /edge-well-known/{service.Id:N}", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain("file_server", caddy, StringComparison.Ordinal);
        Assert.DoesNotContain(publicRoot, caddy, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(dataRoot, "well-known", "public"), caddy, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private WellKnownServiceManager CreateManager(
        HttpMessageHandler handler,
        RecordingRelayProvisioningService? relay = null)
    {
        var options = Options.Create(new EdgeGatewayCoreOptions
        {
            DataRoot = tempRoot,
            CaddyConfigPath = Path.Combine(tempRoot, "Caddyfile")
        });

        return new WellKnownServiceManager(
            options,
            new JsonWellKnownServiceStore(options),
            relay ?? new RecordingRelayProvisioningService(),
            new HttpClient(handler));
    }

    private static WellKnownService Service(string domain, string path, string contentType, string body) =>
        new(
            Guid.NewGuid(),
            "Test service",
            domain,
            path,
            contentType,
            body,
            WellKnownSourceType.StaticText,
            true,
            false,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            PublicUrl: WellKnownPath.BuildPublicUrl(domain, path),
            PublicFilePath: WellKnownPath.BuildPublicFilePath(Path.GetTempPath(), domain, path));

    private sealed class StaticResponseHandler(
        HttpStatusCode statusCode,
        string contentType,
        string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingRelayProvisioningService : IEdgeGatewayRelayProvisioningService
    {
        public int RefreshCount { get; private set; }

        public Task<EdgeGatewayCaddyConfigurationResult> RefreshCaddyConfigurationAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.FromResult(new EdgeGatewayCaddyConfigurationResult(true, "Reloaded Caddy with the generated Edge Gateway config.", []));
        }

        public Task<EdgeGatewayCaddyConfigurationResult> RefreshPublishedConfigurationAsync(CancellationToken cancellationToken = default) =>
            RefreshCaddyConfigurationAsync(cancellationToken);

        public Task<EdgeGatewayRelayProvisioningResult> ProvisionRelayAsync(string domainName, bool replaceExistingDnsRecord = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayRelayRemovalResult> RemoveRelayAsync(string domainName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayRelayValidationResult> ValidateRelayAsync(string domainName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayRelayValidationResult> RepairRelayAsync(string domainName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayApplicationSaveResult> AddApplicationAsync(string domainName, string name, string hostLabel, string targetScheme, string targetHost, int targetPort, string accessPolicy, string targetPathPrefix = "", bool isEnabled = true, string allowKnownIps = "", string allowedUsers = "", string allowedGroups = "", bool allowLanOnly = false, string notes = "", bool? usePublicHostHeader = null, bool? stripForwardedFor = null, bool? skipUpstreamTlsVerification = null, string temporaryIpApprovalRecipients = "", string temporaryIpApprovalAllowedCountryCodes = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayApplicationSaveResult> UpdateApplicationAsync(Guid applicationId, string name, string hostLabel, string targetScheme, string targetHost, int targetPort, string accessPolicy, string targetPathPrefix = "", string allowKnownIps = "", string allowedUsers = "", string allowedGroups = "", bool allowLanOnly = false, string notes = "", bool? usePublicHostHeader = null, bool? stripForwardedFor = null, bool? skipUpstreamTlsVerification = null, string temporaryIpApprovalRecipients = "", string temporaryIpApprovalAllowedCountryCodes = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayApplicationSaveResult> PublishApplicationAsync(Guid applicationId, bool replaceExistingDnsRecord = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayApplicationSaveResult> SetApplicationEnabledAsync(Guid applicationId, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayApplicationSaveResult> RemoveApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EdgeGatewayApplicationTestResult> TestApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryEdgeGatewayConfigurationStore(EdgeGatewayConfiguration configuration) : IEdgeGatewayConfigurationStore
    {
        private EdgeGatewayConfiguration current = configuration;

        public Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);

        public Task SaveAsync(EdgeGatewayConfiguration configuration, CancellationToken cancellationToken = default)
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

    private sealed class RecordingDnsService : ICloudflareDnsService
    {
        public List<CloudflareDnsRecord> Records { get; } = [];

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
            var saved = record with { Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id };
            Records.Add(saved);
            return Task.FromResult(saved);
        }

        public Task<CloudflareDnsRecord> UpdateRecordAsync(
            string apiToken,
            string zoneId,
            CloudflareDnsRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(existing => existing.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task DeleteRecordAsync(
            string apiToken,
            string zoneId,
            string recordId,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(existing => existing.Id.Equals(recordId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTunnelService : ICloudflareTunnelService
    {
        public CloudflareTunnelConfiguration Configuration { get; private set; } = new(
            [new CloudflareTunnelRoute(string.Empty, "http_status:404")]);

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
            Configuration = configuration;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(string apiToken, string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudflareTunnel> CreateTunnelAsync(string apiToken, string accountId, string tunnelName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudflareTunnel?> GetTunnelAsync(string apiToken, string accountId, string tunnelId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetTunnelTokenAsync(string apiToken, string accountId, string tunnelId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteTunnelAsync(string apiToken, string accountId, string tunnelId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteTunnelConnectionsAsync(string apiToken, string accountId, string tunnelId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
