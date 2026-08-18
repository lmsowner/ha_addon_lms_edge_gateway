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
            ptr: "laptop.example.home",
            forward: [IPAddress.Parse("192.168.1.42")]);
        var service = new LanClientTrustService(dns, new FakeLatency(5));

        var result = await service.EvaluateAsync(route, "192.168.1.42", cloudflareConnectingIp: "");

        Assert.True(result.IsTrusted);
        Assert.Equal("laptop.example.home", result.HostName);
        Assert.Contains("Trusted LAN client", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lan_trust_rejects_cloudflare_public_connecting_ip()
    {
        var route = TrustedRoute();
        var dns = new FakeDns(
            ptr: "laptop.example.home",
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
            ptr: "laptop.example.home",
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
            ptr: "laptop.example.home",
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
            ptr: "laptop.example.home",
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
        var lanTrust = new FakeLanTrust(new LanClientTrustResult(true, "Trusted LAN client laptop.example.home.", "laptop.example.home"));
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(route)),
            new MemoryEdgeGatewayAccessCheckPageStore(),
            lanClientTrustService: lanTrust);

        var result = await service.EvaluateAuthAsync(Context(sourceIp: "192.168.1.42"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("lan-trust:laptop.example.home", result.UserName);
        Assert.True(lanTrust.Called);
    }

    [Fact]
    public async Task Route_auth_skips_mfa_for_configured_known_source_ip()
    {
        var route = new PublishedApplicationDefinition(
            Guid.NewGuid(),
            "Home Assistant",
            "hassio.example.com",
            "http://192.168.1.20:8123",
            "MFA/Passkey",
            true,
            AllowKnownIps: "203.0.113.10",
            SkipAuthenticationForKnownIps: true);
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(route)),
            new MemoryEdgeGatewayAccessCheckPageStore());

        var result = await service.EvaluateAuthAsync(new EdgeGatewayAuthCheckContext(
            "hassio.example.com",
            "https",
            "/",
            "203.0.113.10",
            "hassio.example.com",
            IPAddress.Loopback,
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
            ConnectingIp: "203.0.113.10"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("known-ip:203.0.113.10", result.UserName);
    }

    [Fact]
    public async Task Route_auth_skips_mfa_when_cloudflare_connecting_ip_is_ipv4_mapped()
    {
        var route = new PublishedApplicationDefinition(
            Guid.NewGuid(),
            "Home Assistant",
            "hassio.example.com",
            "http://192.168.1.20:8123",
            "MFA/Passkey",
            true,
            AllowKnownIps: "203.0.113.10",
            SkipAuthenticationForKnownIps: true);
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(route)),
            new MemoryEdgeGatewayAccessCheckPageStore());

        var result = await service.EvaluateAuthAsync(new EdgeGatewayAuthCheckContext(
            "hassio.example.com",
            "https",
            "/",
            "::ffff:203.0.113.10",
            "hassio.example.com",
            IPAddress.Loopback,
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
            ConnectingIp: "::ffff:203.0.113.10"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("known-ip:203.0.113.10", result.UserName);
    }

    private static PublishedApplicationDefinition TrustedRoute() =>
        new(
            Guid.NewGuid(),
            "Home Assistant",
            "hassio.example.home",
            "http://192.168.1.20:8123",
            "MFA/Passkey",
            true,
            LanTrustEnabled: true,
            LanTrustCidrs: "192.168.1.0/24",
            LanTrustDnsSuffixes: "example.home",
            LanTrustRequireForwardConfirm: true);

    private static EdgeGatewayConfiguration Configuration(PublishedApplicationDefinition route) =>
        new(
            [route],
            [],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);

    private static EdgeGatewayAuthCheckContext Context(string sourceIp) =>
        new(
            "hassio.example.home",
            "https",
            "/",
            sourceIp,
            "hassio.example.home",
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
