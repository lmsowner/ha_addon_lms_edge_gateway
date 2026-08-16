using System.Net;
using LMS.EdgeGateway.Core;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class LanClientTrustTests
{
    [Fact]
    public async Task Lan_trust_allows_fcrdns_match_inside_trusted_cidr()
    {
        var route = TrustedRoute();
        var dns = new FakeDns(
            ptr: "laptop.kiernanfamily.co.uk",
            forward: [IPAddress.Parse("192.168.1.42")]);
        var service = new LanClientTrustService(dns, new FakeLatency(5));

        var result = await service.EvaluateAsync(route, "192.168.1.42", cloudflareConnectingIp: "");

        Assert.True(result.IsTrusted);
        Assert.Equal("laptop.kiernanfamily.co.uk", result.HostName);
        Assert.Contains("Trusted LAN client", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lan_trust_rejects_cloudflare_public_connecting_ip()
    {
        var route = TrustedRoute();
        var dns = new FakeDns(
            ptr: "laptop.kiernanfamily.co.uk",
            forward: [IPAddress.Parse("192.168.1.42")]);
        var service = new LanClientTrustService(dns, new FakeLatency(5));

        var result = await service.EvaluateAsync(
            route,
            "192.168.1.42",
            cloudflareConnectingIp: "198.51.100.44");

        Assert.False(result.IsTrusted);
        Assert.Contains("Cloudflare internet clients", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lan_trust_rejects_outside_cidr()
    {
        var route = TrustedRoute();
        var dns = new FakeDns(
            ptr: "laptop.kiernanfamily.co.uk",
            forward: [IPAddress.Parse("10.0.0.8")]);
        var service = new LanClientTrustService(dns, new FakeLatency(5));

        var result = await service.EvaluateAsync(route, "10.0.0.8", cloudflareConnectingIp: "");

        Assert.False(result.IsTrusted);
        Assert.Contains("outside the trusted LAN CIDR", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lan_trust_rejects_untrusted_dns_suffix()
    {
        var route = TrustedRoute();
        var dns = new FakeDns(
            ptr: "laptop.evil.example",
            forward: [IPAddress.Parse("192.168.1.42")]);
        var service = new LanClientTrustService(dns, new FakeLatency(5));

        var result = await service.EvaluateAsync(route, "192.168.1.42", cloudflareConnectingIp: "");

        Assert.False(result.IsTrusted);
        Assert.Contains("trusted DNS suffix", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lan_trust_rejects_failed_forward_confirm()
    {
        var route = TrustedRoute();
        var dns = new FakeDns(
            ptr: "laptop.kiernanfamily.co.uk",
            forward: [IPAddress.Parse("192.168.1.99")]);
        var service = new LanClientTrustService(dns, new FakeLatency(5));

        var result = await service.EvaluateAsync(route, "192.168.1.42", cloudflareConnectingIp: "");

        Assert.False(result.IsTrusted);
        Assert.Contains("did not resolve back", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lan_trust_rejects_when_latency_exceeds_maximum()
    {
        var route = TrustedRoute() with { LanTrustMaxLatencyMilliseconds = 10 };
        var dns = new FakeDns(
            ptr: "laptop.kiernanfamily.co.uk",
            forward: [IPAddress.Parse("192.168.1.42")]);
        var service = new LanClientTrustService(dns, new FakeLatency(40));

        var result = await service.EvaluateAsync(route, "192.168.1.42", cloudflareConnectingIp: "");

        Assert.False(result.IsTrusted);
        Assert.Contains("exceeds the configured maximum", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Route_auth_skips_mfa_for_trusted_lan_client()
    {
        var route = TrustedRoute() with { AccessPolicy = "MFA/Passkey" };
        var lanTrust = new FakeLanTrust(new LanClientTrustResult(true, "Trusted LAN client laptop.kiernanfamily.co.uk.", "laptop.kiernanfamily.co.uk"));
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(route)),
            lanClientTrustService: lanTrust);

        var result = await service.EvaluateAuthAsync(Context(sourceIp: "192.168.1.42"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("lan-trust:laptop.kiernanfamily.co.uk", result.UserName);
        Assert.True(lanTrust.Called);
    }

    private static PublishedApplicationDefinition TrustedRoute() =>
        new(
            Guid.NewGuid(),
            "Home Assistant",
            "hassio.kiernanfamily.co.uk",
            "http://192.168.1.20:8123",
            "MFA/Passkey",
            true,
            LanTrustEnabled: true,
            LanTrustCidrs: "192.168.0.0/20",
            LanTrustDnsSuffixes: "kiernanfamily.co.uk",
            LanTrustRequireForwardConfirm: true);

    private static EdgeGatewayConfiguration Configuration(PublishedApplicationDefinition route) =>
        new(
            [route],
            [],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);

    private static EdgeGatewayAuthCheckContext Context(string sourceIp) =>
        new(
            "hassio.kiernanfamily.co.uk",
            "https",
            "/",
            sourceIp,
            "hassio.kiernanfamily.co.uk",
            IPAddress.Loopback,
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
            ConnectingIp: "");

    private sealed class FakeDns(string? ptr, IReadOnlyList<IPAddress> forward) : IDnsNameResolver
    {
        public Task<string?> ResolvePtrAsync(IPAddress address, CancellationToken cancellationToken = default) =>
            Task.FromResult(ptr);

        public Task<IReadOnlyList<IPAddress>> ResolveForwardAsync(string hostName, CancellationToken cancellationToken = default) =>
            Task.FromResult(forward);
    }

    private sealed class FakeLatency(int? milliseconds) : ILanLatencyProbe
    {
        public Task<int?> MeasureMillisecondsAsync(
            IPAddress address,
            int timeoutMilliseconds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(milliseconds);
    }

    private sealed class FakeLanTrust(LanClientTrustResult result) : ILanClientTrustService
    {
        public bool Called { get; private set; }

        public Task<LanClientTrustResult> EvaluateAsync(
            PublishedApplicationDefinition route,
            string sourceIp,
            string cloudflareConnectingIp,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(result);
        }
    }

    private sealed class InMemoryConfigurationStore(EdgeGatewayConfiguration configuration) : IEdgeGatewayConfigurationStore
    {
        public Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task SaveAsync(EdgeGatewayConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
