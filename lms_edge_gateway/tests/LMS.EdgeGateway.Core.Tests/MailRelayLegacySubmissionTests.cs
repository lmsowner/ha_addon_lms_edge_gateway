using LMS.EdgeGateway.Core;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class MailRelayLegacySubmissionTests
{
    [Fact]
    public void Normalize_legacy_request_ignores_listen_addresses_and_keeps_the_allowlist()
    {
        var normalized = MailRelayProvisioningService.NormalizeLegacyRequest(
            new MailRelayLegacySubmissionRequest(true, ["0.0.0.0", "192.168.1.10"], ["192.168.1.50", "10.0.0.0/24"]));

        Assert.True(normalized.Enabled);
        Assert.Empty(normalized.ListenAddresses);
        Assert.Equal(["192.168.1.50", "10.0.0.0/24"], normalized.AllowedNetworks);
    }

    [Fact]
    public void Build_master_cf_listens_on_all_adapters_when_legacy_port_25_is_enabled()
    {
        var configuration = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with
        {
            AllowLegacyPort25 = true,
            LegacyListenAddresses = ["192.168.1.10"],
            LegacyAllowedNetworks = ["192.168.1.50", "10.0.0.0/24"]
        };

        var master = MailRelayProvisioningService.BuildMasterCf(["127.0.0.1"], configuration);

        Assert.Contains("25 inet n - n - 20 smtpd", master);
        Assert.DoesNotContain("192.168.1.10:25", master);
        Assert.Contains("mynetworks=192.168.1.50,10.0.0.0/24", master);
    }
}
