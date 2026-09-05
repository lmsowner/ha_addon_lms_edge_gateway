using System.Net;
using System.Net.Sockets;

namespace LMS.EdgeGateway.Core;

public sealed class MailRelayPreflightService(
    ICloudflareApiTokenStore tokenStore,
    ICloudflareZoneService zoneService,
    ICloudflareDnsService dnsService,
    IMailRelayHostCommand hostCommand,
    IHttpClientFactory httpClientFactory) : IMailRelayPreflightService
{
    private static readonly string[] PublicMxTargets =
    [
        "gmail-smtp-in.l.google.com",
        "outlook-com.olc.protection.outlook.com",
        "mta5.am0.yahoodns.net"
    ];

    public async Task<MailRelayPreflightResult> InspectAsync(
        bool verifyDnsEdit,
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        var token = await tokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return MissingEdgeGatewayResult(verifyDnsEdit, checkedAtUtc);
        }

        IReadOnlyList<MailRelayCloudflareZoneOption> availableZones = [];
        var checks = new List<MailRelayPreflightCheck>
        {
            Check(MailRelayPreflightCheckKeys.EdgeGateway, "Edge Gateway", MailRelayPreflightCheckState.Pass,
                "CONFIGURED", "The saved Cloudflare token is available to Mail Relay.")
        };

        CloudflareZoneSummary? selectedZone = null;
        string zoneName = string.Empty;
        try
        {
            var zones = await zoneService.ListZonesAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(zones.Error) || zones.Zones.Count == 0)
            {
                throw new InvalidOperationException(zones.Error ?? "No Cloudflare zones were returned for the saved token.");
            }

            availableZones = zones.Zones
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new MailRelayCloudflareZoneOption(
                    item.Id,
                    item.Name.Trim().TrimEnd('.'),
                    item.Status,
                    false,
                    !string.IsNullOrWhiteSpace(cloudflareZoneId) &&
                    item.Id.Equals(cloudflareZoneId.Trim(), StringComparison.Ordinal)))
                .ToArray();

            selectedZone = zones.Zones.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(cloudflareZoneId) &&
                    item.Id.Equals(cloudflareZoneId.Trim(), StringComparison.Ordinal))
                ?? zones.Zones.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).First();
            zoneName = selectedZone.Name.Trim().TrimEnd('.');
            checks[0] = Check(MailRelayPreflightCheckKeys.EdgeGateway, "Edge Gateway", MailRelayPreflightCheckState.Pass,
                "CONFIGURED", $"The saved token provides {availableZones.Count} Cloudflare zone(s); {zoneName} is selected.");
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection",
                MailRelayPreflightCheckState.Pass, "PASS", "The saved Edge Gateway token authenticated successfully."));
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone",
                MailRelayPreflightCheckState.Pass, zoneName, $"The selected zone is accessible. {availableZones.Count} zone(s) are available."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection",
                MailRelayPreflightCheckState.Failed, "FAILED", "Cloudflare rejected the saved token or configured zone."));
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone",
                MailRelayPreflightCheckState.Failed, zoneName, "The configured zone could not be accessed."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.Failed, "FAILED", "DNS records could not be listed."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "Resolve Cloudflare access before testing DNS Edit."));
            AddHostChecksNotAvailable(checks, "Complete the Edge Gateway Cloudflare connection first.");
            return BuildResult(selectedZone?.Id ?? string.Empty, zoneName, availableZones, string.Empty, string.Empty, checks, verifyDnsEdit, checkedAtUtc);
        }

        try
        {
            await dnsService.ListRecordsAsync(token, selectedZone.Id, cancellationToken);
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.Pass, "PASS", "DNS records can be listed through the existing Cloudflare integration."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.Failed, "FAILED", $"DNS records for {zoneName} could not be listed."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "Select a zone with DNS access or update the Edge Gateway token scope."));
            AddHostChecksNotAvailable(checks, "Cloudflare DNS access must pass before host suitability is tested.");
            return BuildResult(selectedZone.Id, zoneName, availableZones, string.Empty, string.Empty, checks, verifyDnsEdit, checkedAtUtc);
        }

        checks.Add(verifyDnsEdit
            ? await VerifyDnsEditAsync(token, selectedZone.Id, zoneName, cancellationToken)
            : Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management", MailRelayPreflightCheckState.NotRun,
                "NOT TESTED", "Run preflight to verify Zone → DNS → Edit with a temporary record."));

        if (!verifyDnsEdit)
        {
            AddHostChecksNotRun(checks);
            return BuildResult(selectedZone.Id, zoneName, availableZones, string.Empty, string.Empty, checks, false, checkedAtUtc);
        }

        var publicIpTask = DetectPublicIpv4Async(cancellationToken);
        var smtpTask = TestOutboundSmtpAsync(cancellationToken);
        var runtimeTask = InspectMailRuntimeAsync(cancellationToken);

        await Task.WhenAll(publicIpTask, smtpTask, runtimeTask);

        var publicIpResult = await publicIpTask;
        var publicAddress = IPAddress.TryParse(publicIpResult.Address, out var parsedPublicAddress)
            ? parsedPublicAddress
            : null;
        checks.Add(Check(
            MailRelayPreflightCheckKeys.PublicIpv4,
            "Public IPv4",
            publicIpResult.Success ? MailRelayPreflightCheckState.Pass : MailRelayPreflightCheckState.Failed,
            publicIpResult.Success ? publicIpResult.Address : "NOT AVAILABLE",
            publicIpResult.Detail));
        checks.Add(await InspectReverseDnsAsync(publicAddress, cancellationToken));
        checks.Add(await smtpTask);
        checks.Add(await runtimeTask);

        var reverseHostname = checks
            .Single(item => item.Key == MailRelayPreflightCheckKeys.ReverseDns)
            .State == MailRelayPreflightCheckState.Pass
                ? checks.Single(item => item.Key == MailRelayPreflightCheckKeys.ReverseDns).Value
                : string.Empty;

        return BuildResult(
            selectedZone.Id,
            zoneName,
            availableZones,
            publicIpResult.Address,
            reverseHostname,
            checks,
            true,
            checkedAtUtc);
    }

    public async Task<MailRelayPublicIpv4DetectionResult> DetectPublicIpv4Async(CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken);
            var value = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1]
                .Trim();

            if (IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IsPrivateIpv4(address))
            {
                return new MailRelayPublicIpv4DetectionResult(
                    true,
                    address.ToString(),
                    "The public egress IPv4 address was detected.");
            }

            return new MailRelayPublicIpv4DetectionResult(
                false,
                string.Empty,
                "A public IPv4 address could not be detected.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new MailRelayPublicIpv4DetectionResult(
                false,
                string.Empty,
                "The public IPv4 check could not reach its detection endpoint.");
        }
    }

    private async Task<MailRelayPreflightCheck> VerifyDnsEditAsync(
        string apiToken,
        string zoneId,
        string zoneName,
        CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var recordName = $"_lms-mail-relay-preflight-{nonce[..12]}.{zoneName}";
        CloudflareDnsRecord? created = null;

        try
        {
            created = await dnsService.CreateRecordAsync(
                apiToken,
                zoneId,
                new CloudflareDnsRecord(
                    string.Empty,
                    zoneId,
                    recordName,
                    "TXT",
                    $"lms-mail-relay-preflight={nonce}",
                    false,
                    1,
                    "Managed by Linux Made Sane Mail Relay (permission preflight)",
                    null),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CloudflareApiException exception)
        {
            var permissionFailure = exception.StatusCode is 401 or 403;
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Failed,
                permissionFailure ? "PERMISSION REQUIRED" : "TEST FAILED",
                permissionFailure
                    ? "Cloudflare rejected DNS record creation for this zone. Confirm the token includes Zone → DNS → Edit and that this zone is in its resource scope."
                    : $"Cloudflare could not create the permission-test record: {exception.Message}");
        }
        catch (Exception exception)
        {
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Failed, "TEST FAILED",
                $"The DNS write test could not complete: {exception.Message}");
        }

        try
        {
            await dnsService.DeleteRecordAsync(apiToken, zoneId, created.Id, cancellationToken);
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Pass, "PASS", "Zone → DNS → Edit was verified and the temporary record was removed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCleanupDnsProbeAsync(apiToken, zoneId, created);
            throw;
        }
        catch
        {
            var cleanedUp = await TryCleanupDnsProbeAsync(apiToken, zoneId, created);
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Pass,
                "PASS",
                cleanedUp
                    ? "DNS Edit was verified by creating a temporary record. Its first removal attempt failed, but the cleanup retry succeeded."
                    : $"DNS Edit was verified by creating {recordName}. Cloudflare did not accept either cleanup attempt; remove that temporary TXT record manually.");
        }
    }

    private async Task<bool> TryCleanupDnsProbeAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Id))
        {
            return true;
        }

        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await dnsService.DeleteRecordAsync(apiToken, zoneId, record.Id, cleanupTimeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<MailRelayPreflightCheck> InspectReverseDnsAsync(
        IPAddress? address,
        CancellationToken cancellationToken)
    {
        if (address is null)
        {
            return Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "A public IPv4 address is required before PTR can be checked.");
        }

        try
        {
            var reverse = await Dns.GetHostEntryAsync(address).WaitAsync(cancellationToken);
            var hostname = reverse.HostName.Trim().TrimEnd('.');
            var forward = await Dns.GetHostAddressesAsync(hostname, cancellationToken);
            var matches = forward.Any(candidate => candidate.Equals(address));

            return matches
                ? Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                    MailRelayPreflightCheckState.Pass, hostname, $"{address} and {hostname} resolve back to each other.")
                : Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                    MailRelayPreflightCheckState.Warning, "MISMATCH", $"PTR resolves to {hostname}, but its A record does not resolve to {address}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                MailRelayPreflightCheckState.Warning, "NOT CONFIGURED", $"Set the provider-managed PTR for {address} to the relay hostname.");
        }
    }

    private static async Task<MailRelayPreflightCheck> TestOutboundSmtpAsync(CancellationToken cancellationToken)
    {
        var attempts = PublicMxTargets.Select(target => CanConnectAsync(target, 25, cancellationToken)).ToArray();
        var results = await Task.WhenAll(attempts);
        var reachable = results.Count(result => result);

        return reachable > 0
            ? Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound TCP/25",
                MailRelayPreflightCheckState.Pass, "OPEN", $"Connected to {reachable} of {PublicMxTargets.Length} public MX targets on TCP/25. Delivery still requires STARTTLS.")
            : Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound TCP/25",
                MailRelayPreflightCheckState.Warning, "BLOCKED", "Many ISPs block outbound port 25. This does not block setup. Mail Relay uses STARTTLS when it can reach a destination; direct MX on :25 may not work from this network.");
    }

    private static async Task<bool> CanConnectAsync(
        string hostname,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(hostname, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<MailRelayPreflightCheck> InspectMailRuntimeAsync(CancellationToken cancellationToken)
    {
        var postfix = await hostCommand.RunAsync("postfix", ["-v"], cancellationToken, timeout: TimeSpan.FromSeconds(8));
        var opendkim = await hostCommand.RunAsync("opendkim", ["-V"], cancellationToken, timeout: TimeSpan.FromSeconds(8));
        var sasl = await hostCommand.RunAsync("saslpasswd2", ["-h"], cancellationToken, timeout: TimeSpan.FromSeconds(8));
        var present = postfix.ExitCode != 127 && opendkim.ExitCode != 127 && sasl.ExitCode != 127;
        if (present)
        {
            return Check(MailRelayPreflightCheckKeys.MailRuntime, "Mail runtime",
                MailRelayPreflightCheckState.Pass, "PASS",
                "Postfix, OpenDKIM and SASL are installed in this add-on.");
        }

        return Check(MailRelayPreflightCheckKeys.MailRuntime, "Mail runtime",
            MailRelayPreflightCheckState.Warning, "ADD-ON ONLY",
            "Postfix, OpenDKIM and SASL run inside the Home Assistant add-on image. Local development hosts can preview DNS, but setup starts the MTA only on the add-on.");
    }

    private static MailRelayPreflightResult MissingEdgeGatewayResult(bool dnsEditWasTested, DateTimeOffset checkedAtUtc)
    {
        var checks = new List<MailRelayPreflightCheck>
        {
            Check(MailRelayPreflightCheckKeys.EdgeGateway, "Edge Gateway", MailRelayPreflightCheckState.Failed,
                "NOT CONFIGURED", "Configure Cloudflare in Edge Gateway before setting up Mail Relay."),
            Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection", MailRelayPreflightCheckState.NotAvailable,
                "NOT CONFIGURED", "The Cloudflare token stays owned by Edge Gateway."),
            Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone", MailRelayPreflightCheckState.NotAvailable,
                "NOT AVAILABLE", "Select a Cloudflare zone in Edge Gateway."),
            Check(MailRelayPreflightCheckKeys.DnsList, "DNS access", MailRelayPreflightCheckState.NotAvailable,
                "NOT AVAILABLE", "Configure and test Cloudflare DNS in Edge Gateway."),
            Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management", MailRelayPreflightCheckState.NotAvailable,
                "NOT AVAILABLE", "Configure and test Cloudflare DNS in Edge Gateway.")
        };
        AddHostChecksNotAvailable(checks, "Mail Relay preflight is locked until Edge Gateway is configured.");
        return BuildResult(string.Empty, string.Empty, [], string.Empty, string.Empty, checks, dnsEditWasTested, checkedAtUtc);
    }

    private static void AddHostChecksNotRun(ICollection<MailRelayPreflightCheck> checks)
    {
        checks.Add(Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to detect the public IPv4 address."));
        checks.Add(Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to validate PTR and forward DNS."));
        checks.Add(Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound TCP/25", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to see whether this ISP still allows legacy MX on port 25. A block is only a warning."));
        checks.Add(Check(MailRelayPreflightCheckKeys.MailRuntime, "Mail runtime", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to inspect Postfix, OpenDKIM and SASL."));
    }

    private static void AddHostChecksNotAvailable(ICollection<MailRelayPreflightCheck> checks, string detail)
    {
        checks.Add(Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound TCP/25", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.MailRuntime, "Mail runtime", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
    }

    private static MailRelayPreflightResult BuildResult(
        string zoneId,
        string zoneName,
        IReadOnlyList<MailRelayCloudflareZoneOption> availableZones,
        string publicIpAddress,
        string reverseDnsHostname,
        IReadOnlyList<MailRelayPreflightCheck> checks,
        bool dnsEditWasTested,
        DateTimeOffset checkedAtUtc) =>
        new(
            zoneId,
            zoneName,
            availableZones,
            string.IsNullOrWhiteSpace(zoneName) ? string.Empty : $"smtp.{zoneName}",
            publicIpAddress,
            reverseDnsHostname,
            checks,
            dnsEditWasTested,
            checkedAtUtc);

    private static MailRelayPreflightCheck Check(
        string key,
        string label,
        MailRelayPreflightCheckState state,
        string value,
        string detail) =>
        new(key, label, state, value, detail);

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
