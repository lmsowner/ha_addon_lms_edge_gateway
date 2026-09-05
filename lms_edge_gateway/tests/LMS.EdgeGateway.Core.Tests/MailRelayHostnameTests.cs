using LMS.EdgeGateway.Core;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class MailRelayHostnameTests
{
    [Fact]
    public void Suggests_smtp_when_the_name_is_free()
    {
        var suggestion = MailRelayProvisioningService.SuggestMailHostname(
            "example.com",
            [],
            "203.0.113.10");

        Assert.True(suggestion.Available);
        Assert.Equal("smtp", suggestion.Label);
        Assert.Equal("smtp.example.com", suggestion.Hostname);
        Assert.Empty(suggestion.TakenHostnames);
        Assert.Contains("is free", suggestion.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skips_to_relay_when_smtp_is_taken()
    {
        var suggestion = MailRelayProvisioningService.SuggestMailHostname(
            "example.com",
            [Record("smtp.example.com", "CNAME", "mail.protection.outlook.com")],
            "203.0.113.10");

        Assert.True(suggestion.Available);
        Assert.Equal("relay.example.com", suggestion.Hostname);
        Assert.Equal(["smtp.example.com"], suggestion.TakenHostnames);
    }

    [Fact]
    public void Reuses_smtp_when_it_already_points_at_this_relay()
    {
        var suggestion = MailRelayProvisioningService.SuggestMailHostname(
            "example.com",
            [Record("smtp.example.com", "A", "203.0.113.10")],
            "203.0.113.10");

        Assert.True(suggestion.Available);
        Assert.Equal("smtp.example.com", suggestion.Hostname);
        Assert.Contains("already points at this relay", suggestion.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Treats_proxied_or_foreign_a_records_as_taken()
    {
        var suggestion = MailRelayProvisioningService.SuggestMailHostname(
            "example.com",
            [
                Record("smtp.example.com", "A", "198.51.100.20", proxied: true),
                Record("relay.example.com", "A", "198.51.100.20"),
                Record("mail.example.com", "AAAA", "2001:db8::1")
            ],
            "203.0.113.10");

        Assert.False(suggestion.Available);
        Assert.Equal(3, suggestion.TakenHostnames.Count);
    }

    [Fact]
    public void Expands_a_bare_label_to_the_sending_domain()
    {
        Assert.Equal(
            "relay.example.com",
            MailRelayProvisioningService.NormalizeMailHostname("relay", "example.com"));
    }

    private static CloudflareDnsRecord Record(string name, string type, string content, bool proxied = false) =>
        new("id", "zone", name, type, content, proxied, 1, string.Empty, null);
}
