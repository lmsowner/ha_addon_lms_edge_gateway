using LMS.EdgeGateway.Core;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class MailRelayExistingMailTests
{
    [Fact]
    public void Microsoft_365_coexistence_keeps_mx_and_merges_spf()
    {
        var existing = new MailRelayExistingEmailConfiguration(
            "contoso.com",
            MailRelayExistingProvider.Microsoft365,
            ["contoso-com.mail.protection.outlook.com"],
            "v=spf1 include:spf.protection.outlook.com -all",
            "v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all",
            1,
            ["selector1._domainkey.contoso.com"],
            "v=DMARC1; p=reject",
            "REJECT",
            MailRelayDeliveryMode.DirectInternet);

        Assert.True(existing.HasExistingMail);
        Assert.Equal("Microsoft 365", existing.ProviderLabel);
        Assert.Contains("will not change MX", existing.CoexistenceSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("include:spf.protection.outlook.com", existing.CoexistenceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_lms_spf_ip_leaves_office_365_include()
    {
        var remaining = MailRelayProvisioningService.RemoveLmsSpfIpv4Authorization(
            "v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all",
            "203.0.113.10");

        Assert.Equal("v=spf1 include:spf.protection.outlook.com -all", remaining);
    }
}
