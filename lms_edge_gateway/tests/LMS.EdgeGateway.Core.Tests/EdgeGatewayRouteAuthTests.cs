using System.Net;
using System.Reflection;
using System.Security.Claims;
using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class EdgeGatewayRouteAuthTests
{
    [Fact]
    public async Task Protected_route_without_mfa_session_redirects_to_login()
    {
        var service = new EdgeGatewayRouteAuthService(new InMemoryConfigurationStore(Configuration(Route("MFA/Passkey"))));

        var result = await service.EvaluateAuthAsync(Context(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Equal(302, result.StatusCode);
        Assert.NotNull(result.RedirectLocation);
        Assert.StartsWith("/login?", result.RedirectLocation, StringComparison.Ordinal);
        var decodedLocation = Uri.UnescapeDataString(Uri.UnescapeDataString(result.RedirectLocation));
        Assert.Contains("/edge-auth/return", decodedLocation);
        Assert.Contains("https://hassio.example.com/", decodedLocation);
    }

    [Fact]
    public async Task Protected_route_with_mfa_session_is_allowed()
    {
        var service = new EdgeGatewayRouteAuthService(new InMemoryConfigurationStore(Configuration(Route("MFA/Passkey"))));
        var principal = Principal(new Claim("amr", "otp"));

        var result = await service.EvaluateAuthAsync(Context(principal));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("agent@linuxmadesane.online", result.UserEmail);
        Assert.Equal("agent@linuxmadesane.online", result.UserName);
    }

    [Fact]
    public async Task Protected_route_with_passkey_session_is_allowed()
    {
        var service = new EdgeGatewayRouteAuthService(new InMemoryConfigurationStore(Configuration(Route("MFA/Passkey"))));
        var principal = Principal(new Claim("amr", "passkey"));

        var result = await service.EvaluateAuthAsync(Context(principal));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("agent@linuxmadesane.online", result.UserEmail);
    }

    [Fact]
    public async Task Pass_through_route_without_session_is_allowed()
    {
        var service = new EdgeGatewayRouteAuthService(new InMemoryConfigurationStore(Configuration(Route("Pass Through"))));

        var result = await service.EvaluateAuthAsync(Context(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Pass-through route.", result.Reason);
    }

    [Fact]
    public async Task Temporary_ip_approval_route_uses_approval_service()
    {
        var approvalService = new RecordingTemporaryIpApprovalService(
            new TemporaryIpApprovalEvaluationResult(false, "Approval email sent."));
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(Route(EdgeGatewayAccessPolicies.TemporaryIpApproval))),
            approvalService);

        var result = await service.EvaluateAuthAsync(Context(
            new ClaimsPrincipal(new ClaimsIdentity()),
            connectingIp: "198.51.100.44",
            countryCode: "GB"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Approval email sent.", result.Reason);
        Assert.NotNull(approvalService.LastContext);
        Assert.Equal("198.51.100.44", approvalService.LastContext.SourceIp);
        Assert.Equal("GB", approvalService.LastContext.CountryCode);
    }

    [Fact]
    public async Task Temporary_ip_approval_route_can_hide_denial_with_not_found()
    {
        var approvalService = new RecordingTemporaryIpApprovalService(
            new TemporaryIpApprovalEvaluationResult(false, "Approval email sent."));
        var route = Route(EdgeGatewayAccessPolicies.TemporaryIpApproval) with
        {
            TemporaryIpApprovalUseNotFoundResponse = true
        };
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(route)),
            approvalService);

        var result = await service.EvaluateAuthAsync(Context(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Equal(404, result.StatusCode);
        Assert.True(result.SuppressResponseBody);
        Assert.Empty(result.Reason);
        Assert.NotNull(approvalService.LastContext);
    }

    [Fact]
    public async Task Temporary_ip_approval_allows_when_service_has_active_grant()
    {
        var approvalService = new RecordingTemporaryIpApprovalService(
            new TemporaryIpApprovalEvaluationResult(true, "Temporary IP approval allowed."));
        var service = new EdgeGatewayRouteAuthService(
            new InMemoryConfigurationStore(Configuration(Route(EdgeGatewayAccessPolicies.TemporaryIpApproval))),
            approvalService);

        var result = await service.EvaluateAuthAsync(Context(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("temporary-ip:203.0.113.10", result.UserName);
    }

    [Fact]
    public async Task Safe_return_target_must_match_enabled_route()
    {
        var service = new EdgeGatewayRouteAuthService(new InMemoryConfigurationStore(Configuration(Route("MFA/Passkey"))));

        Assert.True(await service.IsSafeReturnTargetAsync("https://hassio.example.com/"));
        Assert.True(await service.IsSafeReturnTargetAsync("https://hassio.example.com/auth/authorize?client_id=http://example"));
        Assert.False(await service.IsSafeReturnTargetAsync("https://other.example.com/"));
        Assert.False(await service.IsSafeReturnTargetAsync("https://hassio.example.com/login"));
        Assert.False(await service.IsSafeReturnTargetAsync("https://hassio.example.com/lmshaauth/login"));
    }

    [Fact]
    public void Generated_caddyfile_calls_lms_forward_auth_for_protected_routes()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route("MFA/Passkey"));
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.Contains("http://:18080 {", caddyfile, StringComparison.Ordinal);
        Assert.Contains("bind 127.0.0.1", caddyfile, StringComparison.Ordinal);
        Assert.Contains("forward_auth 127.0.0.1:5299", caddyfile, StringComparison.Ordinal);
        Assert.Contains("uri /edge-auth/check", caddyfile, StringComparison.Ordinal);
        Assert.Contains("copy_headers X-LMS-User X-LMS-Email X-LMS-Groups", caddyfile, StringComparison.Ordinal);
        Assert.Contains("path /login /login/* /lmshaauth/login /lmshaauth/email-otp /lmshaauth/logout /edge-auth/* /api/passkeys/login/* /api/passkeys/me/* /api/passkeys/register/complete", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/login", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/logout", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/*", caddyfile, StringComparison.Ordinal);
        Assert.Contains("respond 404", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("No Linux Made Sane Edge Gateway route matched this hostname.", caddyfile, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy 127.0.0.1:5299", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_for_pass_through_route_skips_lms_forward_auth()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route("Pass Through"));
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.DoesNotContain("forward_auth", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:5299", caddyfile, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy http://192.168.1.20:8123", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_for_temporary_ip_approval_route_calls_lms_forward_auth()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route(EdgeGatewayAccessPolicies.TemporaryIpApproval));
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.Contains("forward_auth 127.0.0.1:5299", caddyfile, StringComparison.Ordinal);
        Assert.Contains("uri /edge-auth/check", caddyfile, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy 127.0.0.1:5299", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_for_home_assistant_preserves_public_proxy_headers()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route("Pass Through"));
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.Contains("header_up Host {host}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up X-Real-IP {remote_host}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up X-Forwarded-Host {host}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up X-Forwarded-Proto https", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up X-Forwarded-Port 443", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("header_up Host {upstream_hostport}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up -X-Forwarded-For", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_for_https_home_assistant_skips_upstream_tls_verification()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route("Pass Through") with
        {
            UpstreamUrl = "https://192.168.15.3:8123"
        });
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.Contains("reverse_proxy https://192.168.15.3:8123", caddyfile, StringComparison.Ordinal);
        Assert.Contains("transport http", caddyfile, StringComparison.Ordinal);
        Assert.Contains("tls_insecure_skip_verify", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_for_home_assistant_public_https_upstream_uses_upstream_host_header()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route("Pass Through") with
        {
            PublicHostname = "hassio.gosmore.net",
            UpstreamUrl = "https://hassio.kiernanfamily.co.uk:443",
            UsePublicHostHeader = true
        });
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.Contains("reverse_proxy https://hassio.kiernanfamily.co.uk:443", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up Host {upstream_hostport}", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("header_up Host {host}", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_caddyfile_honors_explicit_proxy_options()
    {
        var service = new EdgeGatewayRelayProvisioningService(
            Options.Create(new EdgeGatewayCoreOptions
            {
                CaddyLocalServiceUrl = "http://localhost:18080",
                LmsForwardAuthPort = 5299
            }),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var configuration = Configuration(Route("Pass Through") with
        {
            Name = "Router UI",
            PublicHostname = "router.example.com",
            UpstreamUrl = "https://192.168.15.1:8443",
            UsePublicHostHeader = true,
            StripForwardedFor = false,
            SkipUpstreamTlsVerification = true
        });
        var method = typeof(EdgeGatewayRelayProvisioningService).GetMethod(
            "GenerateCaddyfile",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var caddyfile = Assert.IsType<string>(method!.Invoke(service, [configuration]));

        Assert.Contains("header_up Host {host}", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("header_up -X-Forwarded-For", caddyfile, StringComparison.Ordinal);
        Assert.Contains("tls_insecure_skip_verify", caddyfile, StringComparison.Ordinal);
    }

    private static EdgeGatewayAuthCheckContext Context(
        ClaimsPrincipal principal,
        string connectingIp = "",
        string countryCode = "") =>
        new(
            "hassio.example.com",
            "https",
            "/",
            "203.0.113.10",
            "127.0.0.1:5000",
            IPAddress.Loopback,
            principal,
            connectingIp,
            countryCode,
            "Plex/1.0");

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "agent@linuxmadesane.online"),
                new Claim(ClaimTypes.Email, "agent@linuxmadesane.online"),
                .. claims
            ],
            "Cookies"));

    private static EdgeGatewayConfiguration Configuration(PublishedApplicationDefinition route) =>
        new(
            [route],
            [],
            new CloudflareTunnelState("tunnel", "account", "tunnel-id", true, DateTimeOffset.UtcNow, "account-id"),
            DateTimeOffset.UtcNow);

    private static PublishedApplicationDefinition Route(string accessPolicy) =>
        new(
            Guid.NewGuid(),
            "Home Assistant",
            "hassio.example.com",
            "http://192.168.1.20:8123",
            accessPolicy,
            true);

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

    private sealed class RecordingTemporaryIpApprovalService(
        TemporaryIpApprovalEvaluationResult result) : IEdgeGatewayTemporaryIpApprovalService
    {
        public TemporaryIpApprovalCheckContext? LastContext { get; private set; }

        public Task<TemporaryIpApprovalEvaluationResult> EvaluateAsync(
            PublishedApplicationDefinition route,
            TemporaryIpApprovalCheckContext context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(result);
        }

        public Task<TemporaryIpApprovalCompletionResult> ApproveAsync(
            string token,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TemporaryIpApprovalCompletionResult(false, "Not implemented", "Not implemented."));
    }
}
