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

    [Fact]
    public void Merges_office_365_spf_with_relay_ip()
    {
        var merged = MailRelayProvisioningService.MergeSpfAuthorizations(
            ["v=spf1 include:spf.protection.outlook.com -all"],
            "203.0.113.10");

        Assert.Equal("v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all", merged);
    }

    [Fact]
    public void Merges_duplicate_stackmail_and_lms_spf_into_one()
    {
        var merged = MailRelayProvisioningService.MergeSpfAuthorizations(
            [
                "v=spf1 include:spf.stackmail.com a mx -all",
                "v=spf1 ip4:95.172.225.2 -all"
            ],
            "95.172.225.2");

        AssertSpfContains(merged, "ip4:95.172.225.2", "include:spf.stackmail.com", "a", "mx");
        Assert.EndsWith(" -all", merged, StringComparison.Ordinal);
        Assert.Equal(1, CountTerm(merged, "ip4:95.172.225.2"));
    }

    [Fact]
    public void Prefers_strictest_all_when_merging_spf_duplicates()
    {
        var merged = MailRelayProvisioningService.MergeSpfAuthorizations(
            [
                "v=spf1 include:_spf.google.com ~all",
                "v=spf1 ip4:198.51.100.20 -all"
            ],
            "198.51.100.20");

        AssertSpfContains(merged, "ip4:198.51.100.20", "include:_spf.google.com");
        Assert.EndsWith(" -all", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Spf_keeper_prefers_provider_include_over_lms_only_record()
    {
        var stackmail = Record("spf-stack", "example.com", "v=spf1 include:spf.stackmail.com a mx -all");
        var lmsOnly = Record("spf-lms", "example.com", "v=spf1 ip4:95.172.225.2 -all");

        var keeper = MailRelayProvisioningService.SelectSpfKeeper([lmsOnly, stackmail], trackedRecordId: "spf-lms");

        Assert.Equal("spf-stack", keeper.Id);
    }

    [Fact]
    public void Analyze_spf_detects_quoted_cloudflare_txt_duplicates()
    {
        var records = new[]
        {
            Record("spf-stack", "linuxmadesane.online", "\"v=spf1 include:spf.stackmail.com a mx -all\""),
            Record("spf-lms", "linuxmadesane.online", "v=spf1 ip4:95.172.225.2 -all"),
            Record("brevo", "linuxmadesane.online", "brevo-code:abc")
        };

        var analysis = MailRelayProvisioningService.AnalyzeSpf(records, "linuxmadesane.online", "95.172.225.2");

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.SourceRecordCount);
        AssertSpfContains(analysis.ProposedValue, "ip4:95.172.225.2", "include:spf.stackmail.com", "a", "mx");
    }

    [Fact]
    public void Normalize_txt_strips_dns_style_quotes()
    {
        Assert.Equal(
            "v=spf1 include:spf.stackmail.com a mx -all",
            MailRelayProvisioningService.NormalizeTxtRecordContent("\"v=spf1 include:spf.stackmail.com a mx -all\""));
    }

    [Fact]
    public void Dmarc_keeper_prefers_reporting_policy_over_bare_p_none()
    {
        var lms = Record("dmarc-lms", "_dmarc.example.com", "v=DMARC1; p=none");
        var brevo = Record("dmarc-brevo", "_dmarc.example.com", "v=DMARC1; p=none; rua=mailto:rua@dmarc.brevo.com");

        var keeper = MailRelayProvisioningService.SelectDmarcKeeper([lms, brevo], trackedRecordId: "dmarc-lms");

        Assert.Equal("dmarc-brevo", keeper.Id);
    }

    [Fact]
    public void Analyze_spf_plans_merge_instead_of_blocking_on_duplicates()
    {
        var records = new[]
        {
            Record("spf-stack", "linuxmadesane.online", "v=spf1 include:spf.stackmail.com a mx -all"),
            Record("spf-lms", "linuxmadesane.online", "v=spf1 ip4:95.172.225.2 -all"),
            Record("brevo", "linuxmadesane.online", "brevo-code:abc")
        };

        var analysis = MailRelayProvisioningService.AnalyzeSpf(records, "linuxmadesane.online", "95.172.225.2");

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.SourceRecordCount);
        AssertSpfContains(analysis.ProposedValue, "ip4:95.172.225.2", "include:spf.stackmail.com", "a", "mx");
        Assert.EndsWith(" -all", analysis.ProposedValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_spf_keeps_single_record_stable_when_already_authorised()
    {
        var records = new[]
        {
            Record("spf", "contoso.com", "v=spf1 include:spf.protection.outlook.com ip4:203.0.113.10 -all")
        };

        var analysis = MailRelayProvisioningService.AnalyzeSpf(records, "contoso.com", "203.0.113.10");

        Assert.Empty(analysis.Errors);
        Assert.Equal(1, analysis.SourceRecordCount);
        Assert.Equal("v=spf1 include:spf.protection.outlook.com ip4:203.0.113.10 -all", analysis.ProposedValue);
    }

    private static void AssertSpfContains(string merged, params string[] terms)
    {
        Assert.StartsWith("v=spf1 ", merged, StringComparison.Ordinal);
        foreach (var term in terms)
        {
            Assert.Contains(term, merged.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int CountTerm(string merged, string term) =>
        merged.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(item => item.Equals(term, StringComparison.OrdinalIgnoreCase));

    private static CloudflareDnsRecord Record(string id, string name, string content) =>
        new(id, "zone", name, "TXT", content, false, 1, string.Empty, null);
}
