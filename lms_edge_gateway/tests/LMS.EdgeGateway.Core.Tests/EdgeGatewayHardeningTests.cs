using System.Net;
using LMS.EdgeGateway.Core;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class EdgeGatewayHardeningTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("172.30.32.10", true)]
    [InlineData("172.30.33.255", true)]
    [InlineData("172.30.232.1", true)]
    [InlineData("172.17.0.2", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void Listen_access_allows_loopback_lan_and_docker_but_not_public_internet(string ip, bool allowed)
    {
        Assert.Equal(allowed, EdgeGatewayListenAccess.IsAllowedRemoteAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Listen_access_allows_null_remote_address()
    {
        Assert.True(EdgeGatewayListenAccess.IsAllowedRemoteAddress(null));
    }

    [Fact]
    public void Canonicalize_maps_ipv4_mapped_addresses()
    {
        Assert.True(EdgeGatewayIpAddress.TryCanonicalize("::ffff:203.0.113.10", out var mapped));
        Assert.Equal(IPAddress.Parse("203.0.113.10"), mapped);
        Assert.True(EdgeGatewayIpAddress.AddressesEqual(
            IPAddress.Parse("::ffff:203.0.113.10"),
            IPAddress.Parse("203.0.113.10")));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.8.0.1", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fe80::abcd", true)]
    [InlineData("203.0.113.10", false)]
    [InlineData("2001:db8::1", false)]
    public void Lan_address_includes_ipv6_ula(string ip, bool expected)
    {
        Assert.Equal(expected, EdgeGatewayIpAddress.IsLanAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Auth_client_address_prefers_canonical_cloudflare_connecting_ip()
    {
        var resolved = AuthClientAddress.Resolve(
            "::ffff:203.0.113.10",
            "198.51.100.1",
            IPAddress.Loopback);

        Assert.Equal("203.0.113.10", resolved);
    }

    [Fact]
    public void Login_rate_limiter_blocks_after_max_failures_and_clears_on_success()
    {
        var limiter = new AuthAttemptRateLimiter(3, TimeSpan.FromMinutes(15));
        const string key = "login-ip:203.0.113.10";

        limiter.RecordFailure(key);
        limiter.RecordFailure(key);
        Assert.False(limiter.IsLimited(key));

        limiter.RecordFailure(key);
        Assert.True(limiter.IsLimited(key));

        limiter.RecordSuccess(key);
        Assert.False(limiter.IsLimited(key));
    }

    [Fact]
    public void Email_otp_send_limiter_allows_three_attempts()
    {
        var limiter = new EmailOtpSendRateLimiter();
        const string key = "otp:user@example.com";

        limiter.RecordFailure(key);
        limiter.RecordFailure(key);
        Assert.False(limiter.IsLimited(key));

        limiter.RecordFailure(key);
        Assert.True(limiter.IsLimited(key));
    }
}
