using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace LMS.EdgeGateway.Core;

public sealed partial class MailRelayProvisioningService(
    IMailRelayPreflightService preflightService,
    ICloudflareApiTokenStore cloudflareTokenStore,
    ICloudflareDnsService cloudflareDnsService,
    IMailRelaySecretStore secretStore,
    IMailRelayStore store,
    IMailRelayHostCommand hostCommand,
    MailRelayPaths paths) : IMailRelayProvisioningService
{
    private const string ManagedDnsComment = "Managed by Linux Made Sane Mail Relay";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public async Task<MailRelaySetupPreview> PreviewAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);
        var errors = Validate(normalized).ToList();
        var preflight = await preflightService.InspectAsync(true, normalized.CloudflareZoneId, cancellationToken);

        if (!preflight.CloudflareDnsReady)
        {
            errors.AddRange(preflight.Checks
                .Where(item => item.Key is MailRelayPreflightCheckKeys.CloudflareAuthentication or
                    MailRelayPreflightCheckKeys.CloudflareZone or MailRelayPreflightCheckKeys.DnsList or MailRelayPreflightCheckKeys.DnsEdit)
                .Where(item => item.State is MailRelayPreflightCheckState.Failed or MailRelayPreflightCheckState.NotAvailable)
                .Select(item => $"{item.Label}: {item.Detail}"));
        }

        if (!preflight.HostSuitable)
        {
            errors.AddRange(preflight.Checks
                .Where(item => item.Key is MailRelayPreflightCheckKeys.PublicIpv4 or MailRelayPreflightCheckKeys.MailRuntime)
                .Where(item => item.State is MailRelayPreflightCheckState.Failed or MailRelayPreflightCheckState.NotAvailable)
                .Select(item => $"{item.Label}: {item.Detail}"));
        }

        if (!preflight.CloudflareZoneId.Equals(normalized.CloudflareZoneId, StringComparison.Ordinal))
        {
            errors.Add("The selected Cloudflare zone is no longer available to the Edge Gateway token.");
        }

        if (!IsWithinZone(normalized.RelayHostname, preflight.CloudflareZoneName))
        {
            errors.Add($"Relay hostname must be inside the selected Cloudflare zone {preflight.CloudflareZoneName}.");
        }

        if (!IsWithinZone(normalized.SendingDomain, preflight.CloudflareZoneName))
        {
            errors.Add($"Sending domain must be inside the selected Cloudflare zone {preflight.CloudflareZoneName}.");
        }

        var changes = new List<MailRelaySetupChange>();
        MailRelayExistingEmailConfiguration existingEmailConfiguration = new(
            normalized.SendingDomain,
            MailRelayExistingProvider.NoneDetected,
            [],
            null,
            $"v=spf1 ip4:{preflight.PublicIpAddress} -all",
            0,
            [],
            null,
            null,
            MailRelayDeliveryMode.DirectInternet);
        if (preflight.CloudflareDnsReady && errors.Count == 0)
        {
            var token = await GetCloudflareAsync(cancellationToken);
            var records = await cloudflareDnsService.ListRecordsAsync(token, normalized.CloudflareZoneId, cancellationToken);
            var spf = AnalyzeSpf(records, normalized.SendingDomain, preflight.PublicIpAddress);
            var savedDomain = (await store.ListDomainsAsync(cancellationToken)).FirstOrDefault(item =>
                item.DomainName.Equals(normalized.SendingDomain, StringComparison.OrdinalIgnoreCase));
            var trackedRecords = savedDomain is null
                ? []
                : await store.ListDnsRecordsAsync(savedDomain.Id, cancellationToken);
            var selectedDkimName = $"{normalized.DkimSelector}._domainkey.{normalized.SendingDomain}";
            var selectedDkimRecord = records.FirstOrDefault(item =>
                item.Name.TrimEnd('.').Equals(selectedDkimName, StringComparison.OrdinalIgnoreCase) &&
                (item.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) ||
                 item.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)));
            var selectedDkimIsManaged = selectedDkimRecord is not null &&
                (trackedRecords.Any(item =>
                     item.CloudflareRecordId.Equals(selectedDkimRecord.Id, StringComparison.Ordinal) &&
                     item.CreatedByLms) ||
                 selectedDkimRecord.Comment.Equals(ManagedDnsComment, StringComparison.OrdinalIgnoreCase));

            existingEmailConfiguration = BuildExistingEmailConfiguration(records, normalized.SendingDomain, spf);
            changes.Add(BuildAddressChange(records, normalized.RelayHostname, preflight.PublicIpAddress));
            changes.Add(BuildSpfChange(normalized.SendingDomain, spf));
            changes.Add(BuildDkimChange(records, normalized.SendingDomain, normalized.DkimSelector, selectedDkimIsManaged));
            changes.Add(BuildDmarcChange(records, normalized.SendingDomain));
            errors.AddRange(changes.Where(item => item.Kind == MailRelaySetupChangeKind.Blocked).Select(item => item.Detail));
        }

        var warnings = new List<string>();
        if (preflight.GetCheck(MailRelayPreflightCheckKeys.ReverseDns).State != MailRelayPreflightCheckState.Pass)
        {
            warnings.Add($"Set the provider-managed PTR for {preflight.PublicIpAddress} to {normalized.RelayHostname}. Delivery can work before that, but reputation will be weaker.");
        }

        if (preflight.GetCheck(MailRelayPreflightCheckKeys.OutboundSmtp).State != MailRelayPreflightCheckState.Pass)
        {
            warnings.Add("Outbound TCP/25 looks blocked. Full LMS works because that host can reach destination MX on port 25, then STARTTLS. This Home Assistant network cannot. MX hosts such as Outlook do not accept mail on 587.");
        }

        warnings.Add("The initial private submission certificate is LMS-generated. Applications on the Home Assistant LAN must trust that certificate, or use the relay from localhost.");
        return new MailRelaySetupPreview(normalized, preflight, changes, existingEmailConfiguration, warnings, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<MailRelayPublicIpSyncResult> SynchronizePublicIpAsync(
        string detectedPublicIp,
        CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var configuration = await store.GetConfigurationAsync(cancellationToken)
            ?? throw new InvalidOperationException("Set up Mail Relay before synchronising its public IP.");
        if (!configuration.Enabled)
        {
            throw new InvalidOperationException("Mail Relay is not running.");
        }
        if (!IPAddress.TryParse(detectedPublicIp, out var parsedAddress) ||
            parsedAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("The detected public address is not a valid IPv4 address.");
        }

        detectedPublicIp = parsedAddress.ToString();
        var previousPublicIp = configuration.PublicIpAddress;
        var publicIpChanged = !previousPublicIp.Equals(detectedPublicIp, StringComparison.Ordinal);
        var domains = (await store.ListDomainsAsync(cancellationToken))
            .Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.CloudflareZoneId))
            .ToArray();
        if (domains.Length == 0)
        {
            throw new InvalidOperationException("Mail Relay has no configured sending domains to validate.");
        }

        var apiToken = await GetCloudflareAsync(cancellationToken);
        var recordsByZone = new Dictionary<string, IReadOnlyList<CloudflareDnsRecord>>(StringComparer.Ordinal);
        var trackingByDomain = new Dictionary<Guid, IReadOnlyList<MailRelayDnsRecord>>();
        foreach (var domain in domains)
        {
            if (!recordsByZone.ContainsKey(domain.CloudflareZoneId))
            {
                recordsByZone[domain.CloudflareZoneId] = await cloudflareDnsService.ListRecordsAsync(
                    apiToken,
                    domain.CloudflareZoneId,
                    cancellationToken);
            }
            trackingByDomain[domain.Id] = await store.ListDnsRecordsAsync(domain.Id, cancellationToken);
        }

        var checks = new List<MailRelayPublicIpDnsCheck>();
        var changedAnyRecord = false;
        var essentialDnsReady = true;

        var relayOwner = domains
            .Select(domain => new
            {
                Domain = domain,
                Tracking = trackingByDomain[domain.Id].FirstOrDefault(item =>
                    item.Purpose.Equals("Relay hostname", StringComparison.OrdinalIgnoreCase) &&
                    item.Name.TrimEnd('.').Equals(configuration.RelayHostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            })
            .FirstOrDefault(item => item.Tracking is not null);
        if (relayOwner is null)
        {
            essentialDnsReady = false;
            checks.Add(new MailRelayPublicIpDnsCheck(
                "Relay A record",
                configuration.RelayHostname,
                MailRelayDnsStatus.Failed,
                false,
                "LMS has no DNS ownership record for the relay hostname, so it was not changed automatically."));
        }
        else
        {
            var before = recordsByZone[relayOwner.Domain.CloudflareZoneId];
            var tracked = relayOwner.Tracking!;
            var matches = before.Where(item =>
                    item.Type.Equals("A", StringComparison.OrdinalIgnoreCase) &&
                    item.Name.TrimEnd('.').Equals(configuration.RelayHostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var current = before.FirstOrDefault(item => item.Id.Equals(tracked.CloudflareRecordId, StringComparison.Ordinal));
            if (current is null && matches.Length == 1)
            {
                current = matches[0];
            }
            CloudflareDnsRecord? saved = null;
            if (matches.Length > 1)
            {
                essentialDnsReady = false;
                checks.Add(new MailRelayPublicIpDnsCheck(
                    "Relay A record", configuration.RelayHostname, MailRelayDnsStatus.Failed, false,
                    "Multiple A records exist for the relay hostname. LMS left them unchanged."));
            }
            else if (current is null && !tracked.CreatedByLms)
            {
                essentialDnsReady = false;
                checks.Add(new MailRelayPublicIpDnsCheck(
                    "Relay A record", configuration.RelayHostname, MailRelayDnsStatus.Failed, false,
                    "The relay A record is missing, but LMS did not create it, so it was not recreated automatically."));
            }
            else if (current is not null &&
                     !tracked.CreatedByLms &&
                     !tracked.ModifiedByLms &&
                     (!current.Content.Equals(detectedPublicIp, StringComparison.Ordinal) || current.Proxied))
            {
                essentialDnsReady = false;
                checks.Add(new MailRelayPublicIpDnsCheck(
                    "Relay A record", configuration.RelayHostname, MailRelayDnsStatus.Failed, false,
                    "This relay A record existed before LMS and is not LMS-owned, so automatic monitoring did not overwrite it."));
            }
            else
            {
                saved = current is null
                    ? await cloudflareDnsService.CreateRecordAsync(
                        apiToken,
                        relayOwner.Domain.CloudflareZoneId,
                        new CloudflareDnsRecord(
                            string.Empty,
                            relayOwner.Domain.CloudflareZoneId,
                            configuration.RelayHostname,
                            "A",
                            detectedPublicIp,
                            false,
                            1,
                            ManagedDnsComment,
                            null),
                        cancellationToken)
                    : current.Content.Equals(detectedPublicIp, StringComparison.Ordinal) && !current.Proxied
                        ? current
                        : await cloudflareDnsService.UpdateRecordAsync(
                            apiToken,
                            relayOwner.Domain.CloudflareZoneId,
                            current with { Content = detectedPublicIp, Proxied = false },
                            cancellationToken);
                var changed = current is null || !current.Content.Equals(saved.Content, StringComparison.Ordinal) || current.Proxied;
                changedAnyRecord |= changed;
                await SaveDnsOwnershipAsync(relayOwner.Domain.Id, saved, "Relay hostname", before, checkedAt, cancellationToken);
                var publicMatch = await PublicAddressMatchesAsync(configuration.RelayHostname, detectedPublicIp, cancellationToken);
                checks.Add(new MailRelayPublicIpDnsCheck(
                    "Relay A record",
                    configuration.RelayHostname,
                    publicMatch ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending,
                    changed,
                    publicMatch
                        ? $"Public DNS resolves to {detectedPublicIp}."
                        : $"Cloudflare is set to {detectedPublicIp}; public DNS propagation is still pending."));
            }
        }

        foreach (var domain in domains)
        {
            var before = recordsByZone[domain.CloudflareZoneId];
            var trackedRecords = trackingByDomain[domain.Id];
            var spfTracking = trackedRecords.FirstOrDefault(item => item.Purpose.Equals("SPF", StringComparison.OrdinalIgnoreCase));
            var spfRecords = Find(before, "TXT", domain.DomainName)
                .Where(item => item.Content.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            MailRelayDnsStatus spfStatus;
            if (spfTracking is null)
            {
                essentialDnsReady = false;
                spfStatus = MailRelayDnsStatus.Failed;
                checks.Add(new MailRelayPublicIpDnsCheck(
                    "SPF", domain.DomainName, spfStatus, false,
                    "LMS has no ownership record for this shared SPF configuration, so it was left unchanged."));
            }
            else if (spfRecords.Length > 1)
            {
                essentialDnsReady = false;
                spfStatus = MailRelayDnsStatus.Failed;
                checks.Add(new MailRelayPublicIpDnsCheck(
                    "SPF", domain.DomainName, spfStatus, false,
                    "Multiple SPF records exist. Merge them into one record before automatic updates can continue."));
            }
            else
            {
                var current = spfRecords.SingleOrDefault();
                var baseValue = current?.Content ?? string.Empty;
                if (current is not null && (spfTracking.CreatedByLms || spfTracking.ModifiedByLms))
                {
                    baseValue = RemoveLmsSpfIpv4Authorization(baseValue, previousPublicIp);
                }

                var analysisRecords = current is null
                    ? before
                    : before.Select(item => item.Id.Equals(current.Id, StringComparison.Ordinal)
                            ? item with { Content = baseValue }
                            : item)
                        .ToArray();
                var analysis = AnalyzeSpf(analysisRecords, domain.DomainName, detectedPublicIp);
                if (analysis.Errors.Count > 0)
                {
                    essentialDnsReady = false;
                    spfStatus = MailRelayDnsStatus.Failed;
                    checks.Add(new MailRelayPublicIpDnsCheck(
                        "SPF", domain.DomainName, spfStatus, false,
                        $"SPF was not changed: {string.Join(" ", analysis.Errors)}"));
                }
                else
                {
                    var proposed = analysis.ProposedValue;
                    CloudflareDnsRecord saved;
                    if (current is null)
                    {
                        if (!spfTracking.CreatedByLms)
                        {
                            essentialDnsReady = false;
                            spfStatus = MailRelayDnsStatus.Failed;
                            checks.Add(new MailRelayPublicIpDnsCheck(
                                "SPF", domain.DomainName, spfStatus, false,
                                "The SPF record is missing, but LMS did not create it, so it was not recreated automatically."));
                            await store.SaveDomainAsync(domain with { SpfStatus = spfStatus, UpdatedUtc = checkedAt }, cancellationToken);
                            continue;
                        }
                        saved = await cloudflareDnsService.CreateRecordAsync(
                            apiToken,
                            domain.CloudflareZoneId,
                            new CloudflareDnsRecord(string.Empty, domain.CloudflareZoneId, domain.DomainName, "TXT", proposed, false, 1, ManagedDnsComment, null),
                            cancellationToken);
                    }
                    else
                    {
                        saved = current.Content.Equals(proposed, StringComparison.Ordinal)
                            ? current
                            : await cloudflareDnsService.UpdateRecordAsync(
                                apiToken,
                                domain.CloudflareZoneId,
                                current with { Content = proposed, Proxied = false },
                                cancellationToken);
                    }
                    var changed = current is null || !current.Content.Equals(saved.Content, StringComparison.Ordinal);
                    changedAnyRecord |= changed;
                    await SaveDnsOwnershipAsync(domain.Id, saved, "SPF", before, checkedAt, cancellationToken);
                    var publicMatch = await PublicTxtMatchesAsync(domain.DomainName, proposed, cancellationToken);
                    spfStatus = publicMatch ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending;
                    checks.Add(new MailRelayPublicIpDnsCheck(
                        "SPF", domain.DomainName, spfStatus, changed,
                        publicMatch
                            ? $"The shared SPF record authorises {detectedPublicIp} and preserves its other mechanisms."
                            : "Cloudflare has the merged SPF value; public DNS propagation is still pending."));
                }
            }

            var dkimName = $"{domain.CurrentDkimSelector}._domainkey.{domain.DomainName}";
            var dkimStatus = MailRelayDnsStatus.Failed;
            if (string.IsNullOrWhiteSpace(domain.CurrentDkimPrivateKeySecretReference))
            {
                checks.Add(new MailRelayPublicIpDnsCheck("DKIM", dkimName, dkimStatus, false, "The LMS DKIM private key reference is missing."));
            }
            else
            {
                try
                {
                    var privateKey = await secretStore.ResolveAsync(domain.CurrentDkimPrivateKeySecretReference, cancellationToken);
                    if (string.IsNullOrWhiteSpace(privateKey))
                    {
                        throw new InvalidOperationException("The LMS DKIM private key could not be resolved.");
                    }
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(privateKey);
                    var expected = BuildDkimDnsValue(rsa);
                    var matches = before.Where(item =>
                            item.Name.TrimEnd('.').Equals(dkimName, StringComparison.OrdinalIgnoreCase) &&
                            (item.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) || item.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                    if (matches.Length != 1 ||
                        !matches[0].Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) ||
                        !matches[0].Content.Equals(expected, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The Cloudflare LMS DKIM record does not match the private signing key. It was not rewritten because DKIM does not depend on the public IP; open the sending domain to repair it deliberately.");
                    }

                    var publicMatch = await PublicTxtMatchesAsync(dkimName, expected, cancellationToken);
                    dkimStatus = publicMatch ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending;
                    checks.Add(new MailRelayPublicIpDnsCheck(
                        "DKIM", dkimName, dkimStatus, false,
                        publicMatch
                            ? "The public LMS DKIM key matches the private signing key."
                            : "Cloudflare has the correct LMS DKIM key; public DNS propagation is still pending."));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    checks.Add(new MailRelayPublicIpDnsCheck("DKIM", dkimName, dkimStatus, false, exception.Message));
                }
            }

            var dmarcName = $"_dmarc.{domain.DomainName}";
            var dmarcRecords = Find(before, "TXT", dmarcName)
                .Where(item => item.Content.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var dmarcPublicMatch = dmarcRecords.Length == 1 &&
                                   await PublicTxtMatchesAsync(dmarcName, dmarcRecords[0].Content, cancellationToken);
            var dmarcStatus = dmarcRecords.Length == 1
                ? dmarcPublicMatch ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending
                : MailRelayDnsStatus.Failed;
            checks.Add(new MailRelayPublicIpDnsCheck(
                "DMARC", dmarcName, dmarcStatus, false,
                dmarcPublicMatch
                    ? "The existing shared DMARC policy is present and unchanged."
                    : dmarcRecords.Length == 1
                        ? "Cloudflare has the shared DMARC policy unchanged; public DNS propagation is still pending."
                    : dmarcRecords.Length == 0
                        ? "No DMARC policy is currently published. LMS did not invent or weaken a shared policy."
                        : "Multiple DMARC policies are published; consolidate them into one record."));

            await store.SaveDomainAsync(domain with
            {
                SpfStatus = spfStatus,
                DkimStatus = dkimStatus,
                DmarcStatus = dmarcStatus,
                UpdatedUtc = checkedAt
            }, cancellationToken);
        }

        checks.Add(await InspectPtrAlignmentAsync(detectedPublicIp, configuration.RelayHostname, cancellationToken));
        var hasFailure = checks.Any(item => item.Status == MailRelayDnsStatus.Failed);
        var hasWarning = checks.Any(item => item.Status == MailRelayDnsStatus.Warning);
        var hasPending = checks.Any(item => item.Status == MailRelayDnsStatus.Pending);
        var status = hasFailure
            ? MailRelayPublicIpMonitorStatus.Error
            : hasWarning
                ? MailRelayPublicIpMonitorStatus.Warning
                : changedAnyRecord
                    ? MailRelayPublicIpMonitorStatus.Updated
                    : hasPending
                        ? MailRelayPublicIpMonitorStatus.Warning
                        : MailRelayPublicIpMonitorStatus.Healthy;
        var addressWasAccepted = essentialDnsReady;
        var summary = hasFailure
            ? "The public IP check found DNS configuration that LMS could not safely repair. Review the failed records below."
            : publicIpChanged
                ? hasPending
                    ? $"Public IPv4 changed from {previousPublicIp} to {detectedPublicIp}. Cloudflare was updated; public DNS propagation is pending."
                    : $"Public IPv4 changed from {previousPublicIp} to {detectedPublicIp}. LMS updated the relay A record and every sending domain SPF record."
                : changedAnyRecord
                    ? $"Public IPv4 is still {detectedPublicIp}. LMS repaired drift in its managed DNS records."
                    : $"Public IPv4 is still {detectedPublicIp}; Mail Relay DNS matches.";

        configuration = configuration with
        {
            PublicIpAddress = addressWasAccepted ? detectedPublicIp : configuration.PublicIpAddress,
            LastPublicIpCheckUtc = checkedAt,
            LastPublicIpChangeUtc = publicIpChanged && addressWasAccepted ? checkedAt : configuration.LastPublicIpChangeUtc,
            PublicIpMonitorStatus = status,
            PublicIpMonitorDetail = summary,
            UpdatedUtc = checkedAt
        };
        await store.SaveConfigurationAsync(configuration, cancellationToken);
        return new MailRelayPublicIpSyncResult(
            !hasFailure,
            publicIpChanged,
            previousPublicIp,
            detectedPublicIp,
            status,
            checks,
            summary,
            checkedAt);
    }

    public async Task<MailRelaySetupResult> ProvisionAsync(
        MailRelaySetupRequest request,
        IProgress<MailRelaySetupProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = CreateSteps();
        void Report(string key, MailRelaySetupStepState state, string detail)
        {
            var index = steps.FindIndex(item => item.Key == key);
            if (index >= 0)
            {
                steps[index] = steps[index] with { State = state, Detail = detail };
                progress?.Report(steps[index]);
            }
        }

        try
        {
            Report("preflight", MailRelaySetupStepState.Running, "Checking the selected zone and this host.");
            var preview = await PreviewAsync(request, cancellationToken);
            if (!preview.CanInstall)
            {
                throw new InvalidOperationException(preview.Errors.FirstOrDefault() ?? "Mail Relay preflight did not pass.");
            }

            var normalized = preview.Request;
            Report("preflight", MailRelaySetupStepState.Complete, "Cloudflare and public IPv4 passed. Internet delivery matches full LMS: MX lookup, TCP/25, then STARTTLS.");

            var now = DateTimeOffset.UtcNow;
            var configuration = await store.GetConfigurationAsync(cancellationToken) ?? MailRelayConfiguration.CreateDefault(now);
            var domains = await store.ListDomainsAsync(cancellationToken);
            var domain = domains.FirstOrDefault(item => item.DomainName.Equals(normalized.SendingDomain, StringComparison.OrdinalIgnoreCase));

            Report("draft", MailRelaySetupStepState.Running, "Saving the relay hostname, sending domain and access choices before package installation starts.");
            configuration = configuration with
            {
                RelayHostname = normalized.RelayHostname,
                PublicIpAddress = preview.Preflight.PublicIpAddress,
                SubmissionPort = 587,
                AllowTailscale = normalized.AllowTailscale,
                AllowTrustedLan = normalized.AllowTrustedLan,
                AllowPublicSubmission = false,
                UpdatedUtc = now
            };
            domain ??= CreateDraftDomain(configuration.Id, normalized, now);
            domain = domain with { CloudflareZoneId = normalized.CloudflareZoneId };
            await store.SaveConfigurationAsync(configuration, cancellationToken);
            await store.SaveDomainAsync(domain, cancellationToken);
            Report("draft", MailRelaySetupStepState.Complete, $"Saved {normalized.SendingDomain} as a pending sending domain. Setup can be retried without re-entering it.");

            Report("runtime", MailRelaySetupStepState.Running, "Checking Postfix, OpenDKIM and SASL before writing configuration.");
            var runtimeSummary = await EnsureMailRuntimeAsync(cancellationToken);
            Report("runtime", MailRelaySetupStepState.Complete, runtimeSummary);

            var network = await InspectPrivateNetworkAsync(normalized.AllowTailscale, normalized.AllowTrustedLan, cancellationToken);

            Report("keys", MailRelaySetupStepState.Running, "Generating or loading TLS and DKIM key material.");
            var tls = await EnsureTlsMaterialAsync(configuration, normalized, network, cancellationToken);
            configuration = tls.Configuration;
            var dkim = await EnsureDkimMaterialAsync(configuration.Id, domain, normalized, now, cancellationToken);
            domain = dkim.Domain;
            configuration = configuration with
            {
                Enabled = configuration.Enabled,
                RelayHostname = normalized.RelayHostname,
                PublicIpAddress = preview.Preflight.PublicIpAddress,
                SubmissionPort = 587,
                AllowTailscale = normalized.AllowTailscale,
                AllowTrustedLan = normalized.AllowTrustedLan,
                AllowPublicSubmission = false,
                UpdatedUtc = now
            };
            await store.SaveConfigurationAsync(configuration, cancellationToken);
            await store.SaveDomainAsync(domain, cancellationToken);
            Report("keys", MailRelaySetupStepState.Complete, "RSA 2048-bit DKIM and TLS material is held in LMS secret storage.");

            var clients = await store.ListClientsAsync(cancellationToken);
            var client = clients.FirstOrDefault(item => item.Username.Equals(normalized.ApplicationUsername, StringComparison.OrdinalIgnoreCase));
            string? generatedPassword = null;
            if (client is null)
            {
                generatedPassword = GeneratePassword();
                client = new MailRelayClient(
                    Guid.NewGuid(), configuration.Id, normalized.ApplicationName, normalized.ApplicationUsername,
                    HashCredentialPassword(generatedPassword), true, [normalized.SendingDomain], [],
                    configuration.DefaultMessagesPerMinute, configuration.DefaultMessagesPerDay, string.Empty, now, now, null);
            }
            else if (!client.AllowedSenderDomains.Contains(normalized.SendingDomain, StringComparer.OrdinalIgnoreCase))
            {
                client = client with
                {
                    AllowedSenderDomains = client.AllowedSenderDomains.Append(normalized.SendingDomain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    UpdatedUtc = now
                };
            }

            Report("apply", MailRelaySetupStepState.Running, "Writing Postfix, SASL and OpenDKIM configuration for the Home Assistant add-on.");
            var allDomains = domains.Where(item => item.Id != domain.Id).Append(domain).ToArray();
            var allClients = clients.Where(item => item.Id != client.Id).Append(client).ToArray();
            var dkimKeys = new Dictionary<Guid, string> { [domain.Id] = dkim.PrivateKeyPem };
            foreach (var configuredDomain in allDomains.Where(item => item.Id != domain.Id))
            {
                if (string.IsNullOrWhiteSpace(configuredDomain.CurrentDkimPrivateKeySecretReference))
                {
                    throw new InvalidOperationException($"The DKIM key for {configuredDomain.DomainName} is missing from LMS secret storage.");
                }

                var existingKey = await secretStore.ResolveAsync(configuredDomain.CurrentDkimPrivateKeySecretReference, cancellationToken);
                if (string.IsNullOrWhiteSpace(existingKey))
                {
                    throw new InvalidOperationException($"The DKIM key for {configuredDomain.DomainName} could not be resolved from LMS secret storage.");
                }
                dkimKeys[configuredDomain.Id] = existingKey;
            }
            await WriteConfigurationFilesAsync(configuration, network.BindAddresses, tls.CertificatePem, tls.PrivateKeyPem, allDomains, allClients, dkimKeys, cancellationToken);
            Report("apply", MailRelaySetupStepState.Complete, "Managed configuration files are written under the add-on data directory.");

            Report("config", MailRelaySetupStepState.Running, "Writing managed Postfix, SMTP AUTH, sender restriction and OpenDKIM configuration.");
            RestrictConfigurationAccess();
            Report("config", MailRelaySetupStepState.Complete, "Managed configuration is installed with private key access restricted.");

            Report("dns", MailRelaySetupStepState.Running, "Applying the reviewed DNS records through the Edge Gateway Cloudflare connection.");
            var apiToken = await GetCloudflareAsync(cancellationToken);
            var records = await cloudflareDnsService.ListRecordsAsync(apiToken, normalized.CloudflareZoneId, cancellationToken);
            var spfAnalysis = AnalyzeSpf(records, normalized.SendingDomain, preview.Preflight.PublicIpAddress);
            var selectedDkimName = $"{normalized.DkimSelector}._domainkey.{normalized.SendingDomain}";
            var selectedDkimRecord = records.FirstOrDefault(item =>
                item.Name.TrimEnd('.').Equals(selectedDkimName, StringComparison.OrdinalIgnoreCase) &&
                (item.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) || item.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)));
            var trackedDnsRecords = await store.ListDnsRecordsAsync(domain.Id, cancellationToken);
            var selectedDkimIsManaged = selectedDkimRecord is not null &&
                                        (trackedDnsRecords.Any(item =>
                                             item.CloudflareRecordId.Equals(selectedDkimRecord.Id, StringComparison.Ordinal) &&
                                             item.CreatedByLms) ||
                                         selectedDkimRecord.Comment.Equals(ManagedDnsComment, StringComparison.OrdinalIgnoreCase));
            var currentPlans = new[]
            {
                BuildAddressChange(records, normalized.RelayHostname, preview.Preflight.PublicIpAddress),
                BuildSpfChange(normalized.SendingDomain, spfAnalysis),
                BuildDkimChange(records, normalized.SendingDomain, normalized.DkimSelector, selectedDkimIsManaged),
                BuildDmarcChange(records, normalized.SendingDomain)
            };
            var blockedPlan = currentPlans.FirstOrDefault(item => item.Kind == MailRelaySetupChangeKind.Blocked);
            if (blockedPlan is not null)
            {
                throw new InvalidOperationException($"DNS changed after review and is no longer safe to apply: {blockedPlan.Detail}");
            }
            if (!PlansMatch(preview.DnsChanges, currentPlans))
            {
                throw new InvalidOperationException("Mail DNS changed after it was reviewed. No DNS records were written; review the current preserve-and-merge plan again.");
            }

            var addressRecord = await UpsertRecordAsync(apiToken, normalized.CloudflareZoneId, records, "A", normalized.RelayHostname, preview.Preflight.PublicIpAddress, false, cancellationToken);
            var spfValue = spfAnalysis.ProposedValue;
            var spfRecord = await UpsertRecordAsync(apiToken, normalized.CloudflareZoneId, records, "TXT", normalized.SendingDomain, spfValue, false, cancellationToken);
            if (!spfRecord.Content.Equals(spfValue, StringComparison.Ordinal) ||
                !spfRecord.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(term => SpfTermAuthorizesIpv4(term, preview.Preflight.PublicIpAddress)))
            {
                throw new InvalidOperationException("Cloudflare did not return the expected merged SPF record.");
            }
            var dkimName = selectedDkimName;
            var dkimRecord = await UpsertRecordAsync(apiToken, normalized.CloudflareZoneId, records, "TXT", dkimName, dkim.PublicDnsValue, false, cancellationToken);
            var dmarcName = $"_dmarc.{normalized.SendingDomain}";
            var existingDmarc = Find(records, "TXT", dmarcName).FirstOrDefault(item => item.Content.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
            var dmarcRecord = existingDmarc ?? await UpsertRecordAsync(apiToken, normalized.CloudflareZoneId, records, "TXT", dmarcName, "v=DMARC1; p=none", false, cancellationToken);
            var refreshedRecords = await cloudflareDnsService.ListRecordsAsync(apiToken, normalized.CloudflareZoneId, cancellationToken);
            ValidatePreservedMailDns(
                records,
                refreshedRecords,
                normalized.SendingDomain,
                spfValue,
                preview.Preflight.PublicIpAddress,
                dkimName,
                dkim.PublicDnsValue,
                dmarcName,
                dmarcRecord.Content);
            var publicSpfMatches = await PublicTxtMatchesAsync(normalized.SendingDomain, spfValue, cancellationToken);
            var publicDkimMatches = await PublicTxtMatchesAsync(dkimName, dkim.PublicDnsValue, cancellationToken);
            var publicDmarcMatches = await PublicTxtMatchesAsync(dmarcName, dmarcRecord.Content, cancellationToken);
            domain = domain with
            {
                DkimCloudflareRecordId = dkimRecord.Id,
                SpfCloudflareRecordId = spfRecord.Id,
                DmarcCloudflareRecordId = dmarcRecord.Id,
                SpfStatus = publicSpfMatches ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending,
                DkimStatus = publicDkimMatches ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending,
                DmarcStatus = publicDmarcMatches ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Pending,
                UpdatedUtc = now
            };
            var publicDnsSummary = publicSpfMatches && publicDkimMatches && publicDmarcMatches
                ? "Public SPF, DKIM and DMARC now match."
                : "Cloudflare is correct; one or more public TXT answers are still propagating.";
            Report("dns", MailRelaySetupStepState.Complete, $"MX and existing provider records are unchanged. SMTP proxying is off for {addressRecord.Name}. {publicDnsSummary}");

            Report("start", MailRelaySetupStepState.Running, "Applying configuration and starting SMTP submission.");
            await ApplyRuntimeConfigurationAsync(cancellationToken);
            await WaitForListenerAsync(cancellationToken);
            if (generatedPassword is not null)
            {
                await EnsureSaslUserAsync(normalized.RelayHostname, normalized.ApplicationUsername, generatedPassword, cancellationToken);
            }
            Report("start", MailRelaySetupStepState.Complete, BuildRuntimeSummary(network.BindAddresses, configuration));

            Report("security", MailRelaySetupStepState.Running, "Checking that unauthenticated relaying is rejected.");
            var openRelayPassed = await TestOpenRelayRejectedAsync(cancellationToken);
            if (!openRelayPassed)
            {
                await TryStopMailRuntimeAsync(null, cancellationToken);
                throw new InvalidOperationException("The automated open-relay test did not observe a rejection. Mail submission was stopped for safety.");
            }
            Report("security", MailRelaySetupStepState.Complete, "Unauthenticated relay was rejected.");

            configuration = configuration with
            {
                Enabled = true,
                RelayHostname = normalized.RelayHostname,
                PublicIpAddress = preview.Preflight.PublicIpAddress,
                SubmissionPort = 587,
                AllowTailscale = normalized.AllowTailscale,
                AllowTrustedLan = normalized.AllowTrustedLan,
                AllowPublicSubmission = false,
                UpdatedUtc = now
            };
            await store.SaveConfigurationAsync(configuration, cancellationToken);
            await store.SaveDomainAsync(domain, cancellationToken);
            await store.SaveClientAsync(client, cancellationToken);
            await SaveDnsOwnershipAsync(domain.Id, addressRecord, "Relay hostname", records, now, cancellationToken);
            await SaveDnsOwnershipAsync(domain.Id, spfRecord, "SPF", records, now, cancellationToken);
            await SaveDnsOwnershipAsync(domain.Id, dkimRecord, "DKIM", records, now, cancellationToken);
            await SaveDnsOwnershipAsync(domain.Id, dmarcRecord, "DMARC", records, now, cancellationToken);

            Report("save", MailRelaySetupStepState.Complete, "Relay configuration, domain and application were saved.");
            return new MailRelaySetupResult(
                true, normalized.RelayHostname, network.SubmissionHost, 587, normalized.ApplicationUsername,
                generatedPassword, normalized.SendingDomain, normalized.DkimSelector, true, steps.ToArray(),
                generatedPassword is null
                    ? "Mail Relay was updated. The existing application password was not changed."
                    : "Mail Relay is running. Copy the generated application password now; LMS will not show it again.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var running = steps.FindIndex(item => item.State == MailRelaySetupStepState.Running);
            if (running >= 0)
            {
                steps[running] = steps[running] with { State = MailRelaySetupStepState.Failed, Detail = exception.Message };
                progress?.Report(steps[running]);
            }

            return new MailRelaySetupResult(false, string.Empty, string.Empty, 587, string.Empty, null, string.Empty, string.Empty, false, steps.ToArray(), exception.Message);
        }
    }

    public async Task<MailRelayLegacySubmissionResult> ConfigureLegacySubmissionAsync(
        MailRelayLegacySubmissionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        if (configuration?.Enabled != true)
        {
            return new(false, configuration, "Set up Mail Relay before enabling a legacy device listener.");
        }

        MailRelayLegacySubmissionRequest normalized;
        try
        {
            normalized = NormalizeLegacyRequest(request);
        }
        catch (InvalidOperationException exception)
        {
            return new(false, configuration, exception.Message);
        }

        var domains = (await store.ListDomainsAsync(cancellationToken)).Where(item => item.Enabled).ToArray();
        if (normalized.Enabled && domains.Length == 0)
        {
            return new(false, configuration, "Add at least one configured sending domain before enabling legacy devices.");
        }

        try
        {
            progress?.Report("Checking the source allowlist…");
            var candidate = configuration with
            {
                AllowLegacyPort25 = normalized.Enabled,
                LegacyListenAddresses = [],
                LegacyAllowedNetworks = normalized.Enabled ? normalized.AllowedNetworks : [],
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            var submissionNetwork = await InspectPrivateNetworkAsync(
                candidate.AllowTailscale,
                candidate.AllowTrustedLan,
                cancellationToken);

            progress?.Report("Writing the restricted Postfix listener configuration…");
            await InstallRuntimePolicyAsync(candidate, submissionNetwork.BindAddresses, domains, cancellationToken);

            progress?.Report("Applying Mail Relay configuration and reloading services…");
            await ApplyRuntimeConfigurationAsync(cancellationToken);
            await WaitForListenerAsync(cancellationToken);

            progress?.Report("Checking authenticated submission and the legacy source filter…");
            await RunRequiredAsync(
                "postfix",
                ["check"],
                "Validate the Mail Relay configuration",
                cancellationToken,
                timeout: TimeSpan.FromSeconds(30));
            if (!await TestOpenRelayRejectedAsync(cancellationToken))
            {
                throw new InvalidOperationException("Authenticated TCP/587 no longer rejected anonymous relay attempts.");
            }
            if (candidate.AllowLegacyPort25)
            {
                await VerifyLegacyPolicyAsync(candidate, cancellationToken);
                await VerifyListenerAsync("127.0.0.1", cancellationToken);
            }

            await store.SaveConfigurationAsync(candidate, cancellationToken);
            progress?.Report(candidate.AllowLegacyPort25
                ? "Legacy TCP/25 is running and restricted to the configured sources."
                : "Legacy TCP/25 is disabled; authenticated TCP/587 is unchanged.");
            return new(
                true,
                candidate,
                candidate.AllowLegacyPort25
                    ? $"Unauthenticated TCP/25 is listening on all adapters and accepts only {string.Join(", ", candidate.EffectiveLegacyAllowedNetworks)}."
                    : "Unauthenticated TCP/25 has been disabled.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                var submissionNetwork = await InspectPrivateNetworkAsync(
                    configuration.AllowTailscale,
                    configuration.AllowTrustedLan,
                    cancellationToken);
                await InstallRuntimePolicyAsync(configuration, submissionNetwork.BindAddresses, domains, cancellationToken);
                await ApplyRuntimeConfigurationAsync(cancellationToken);
                await WaitForListenerAsync(cancellationToken);
            }
            catch
            {
                // Preserve the original, actionable error. A normal setup retry will regenerate all managed files.
            }

            return new(false, configuration, $"Legacy device access was not changed: {FirstUsefulLine(exception.Message)}");
        }
    }

    public async Task<MailRelayRemovalResult> RemoveAsync(
        MailRelayRemovalRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        if (configuration is null)
        {
            return new(false, null, [], [], "Mail Relay is not configured.");
        }
        if (!request.Confirmed)
        {
            return new(false, configuration, [], [], "Confirm the removal choices before continuing.");
        }
        if (!request.RemoveContainer && !request.RemoveApplicationCredentials &&
            !request.RemoveManagedDnsRecords && !request.RemoveDkimKeys)
        {
            return new(false, configuration, [], [], "Choose at least one Mail Relay item to remove.");
        }
        if (!request.RemoveContainer && (request.RemoveApplicationCredentials || request.RemoveDkimKeys))
        {
            return new(false, configuration, [], [], "Remove the Mail Relay runtime when removing its credentials or DKIM keys.");
        }

        var domains = await store.ListDomainsAsync(cancellationToken);
        if (request.RemoveManagedDnsRecords)
        {
            var domainsWithoutZone = domains
                .Where(domain => domain.Enabled && string.IsNullOrWhiteSpace(domain.CloudflareZoneId))
                .Select(domain => domain.DomainName)
                .ToArray();
            if (domainsWithoutZone.Length > 0)
            {
                return new(
                    false,
                    configuration,
                    [],
                    [],
                    $"Re-apply settings for {string.Join(", ", domainsWithoutZone)} before DNS removal so LMS can attach the exact Cloudflare zone ID.");
            }
        }

        var changes = new List<string>();
        var warnings = new List<string>();
        try
        {
            progress?.Report("Stopping Mail Relay before changing its delivery identity…");
            configuration = configuration with
            {
                Enabled = false,
                AllowLegacyPort25 = false,
                LegacyListenAddresses = [],
                LegacyAllowedNetworks = [],
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await TryStopMailRuntimeAsync(warnings, cancellationToken);
            changes.Add(request.RemoveContainer ? "Mail Relay runtime removed" : "Mail Relay services stopped");
            await store.SaveConfigurationAsync(configuration, cancellationToken);

            if (request.RemoveManagedDnsRecords)
            {
                progress?.Report("Reading current Cloudflare DNS and removing only LMS-owned changes…");
                var apiToken = await GetCloudflareAsync(cancellationToken);
                foreach (var domain in domains.Where(item => !string.IsNullOrWhiteSpace(item.CloudflareZoneId)))
                {
                    await RemoveManagedDomainDnsAsync(
                        apiToken,
                        configuration,
                        domain,
                        changes,
                        warnings,
                        cancellationToken);
                }
            }

            if (request.RemoveDkimKeys)
            {
                progress?.Report("Removing Mail Relay DKIM secrets…");
                foreach (var secretReference in domains
                             .SelectMany(domain => new[]
                             {
                                 domain.CurrentDkimPrivateKeySecretReference,
                                 domain.PreviousDkimPrivateKeySecretReference
                             })
                             .Where(reference => !string.IsNullOrWhiteSpace(reference))
                             .Distinct(StringComparer.Ordinal))
                {
                    await secretStore.DeleteAsync(secretReference, cancellationToken);
                }
                changes.Add("DKIM private keys removed from LMS secret storage");
            }

            if (request.RemoveApplicationCredentials)
            {
                progress?.Report("Removing application credentials…");
                foreach (var client in await store.ListClientsAsync(cancellationToken))
                {
                    await store.DeleteClientAsync(client.Id, cancellationToken);
                }
                TryDeleteFile(paths.SaslDatabasePath, "SASL credential database", warnings);
                changes.Add("Application credentials removed");
            }

            if (request.RemoveContainer)
            {
                progress?.Report("Removing local Mail Relay runtime files…");
                TryDeleteDirectory(paths.ConfigDirectory, "Mail Relay managed configuration", warnings);
                await DeleteSecretIfPresentAsync(configuration.TlsCertificateSecretReference, cancellationToken);
                await DeleteSecretIfPresentAsync(configuration.TlsPrivateKeySecretReference, cancellationToken);
                await DeleteSecretIfPresentAsync(configuration.SmarthostPasswordSecretReference, cancellationToken);
                configuration = configuration with
                {
                    TlsCertificateSecretReference = null,
                    TlsPrivateKeySecretReference = null,
                    DeliveryMode = MailRelayDeliveryMode.DirectInternet,
                    UseSmarthost = false,
                    SmarthostHostname = string.Empty,
                    SmarthostUsername = string.Empty,
                    SmarthostPasswordSecretReference = null,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                await store.SaveConfigurationAsync(configuration, cancellationToken);
                changes.Add("Local relay configuration and transient queue removed");
            }

            foreach (var domain in domains)
            {
                await store.SaveDomainAsync(domain with
                {
                    Enabled = false,
                    CurrentDkimPrivateKeySecretReference = request.RemoveDkimKeys ? null : domain.CurrentDkimPrivateKeySecretReference,
                    PreviousDkimPrivateKeySecretReference = request.RemoveDkimKeys ? null : domain.PreviousDkimPrivateKeySecretReference,
                    DkimCloudflareRecordId = request.RemoveManagedDnsRecords ? null : domain.DkimCloudflareRecordId,
                    SpfCloudflareRecordId = request.RemoveManagedDnsRecords ? null : domain.SpfCloudflareRecordId,
                    DmarcCloudflareRecordId = request.RemoveManagedDnsRecords ? null : domain.DmarcCloudflareRecordId,
                    SpfStatus = MailRelayDnsStatus.NotChecked,
                    DkimStatus = MailRelayDnsStatus.NotChecked,
                    DmarcStatus = MailRelayDnsStatus.NotChecked,
                    UpdatedUtc = DateTimeOffset.UtcNow
                }, cancellationToken);
            }

            progress?.Report("Mail Relay removal complete.");
            var summary = warnings.Count == 0
                ? "Mail Relay was removed. Existing MX, provider DKIM and DMARC records were preserved."
                : "Mail Relay was removed locally. Some changed DNS records were deliberately preserved; review the warnings.";
            return new(true, configuration, changes, warnings, summary);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, configuration, changes, warnings, $"Mail Relay removal stopped safely: {FirstUsefulLine(exception.Message)}");
        }
    }

    private async Task<(MailRelayConfiguration Configuration, string CertificatePem, string PrivateKeyPem)> EnsureTlsMaterialAsync(
        MailRelayConfiguration configuration,
        MailRelaySetupRequest request,
        (IReadOnlyList<string> BindAddresses, string SubmissionHost) network,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuration.TlsCertificateSecretReference) &&
            !string.IsNullOrWhiteSpace(configuration.TlsPrivateKeySecretReference) &&
            configuration.RelayHostname.Equals(request.RelayHostname, StringComparison.OrdinalIgnoreCase))
        {
            var storedCertificate = await secretStore.ResolveAsync(configuration.TlsCertificateSecretReference, cancellationToken);
            var privateKey = await secretStore.ResolveAsync(configuration.TlsPrivateKeySecretReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(storedCertificate) && !string.IsNullOrWhiteSpace(privateKey))
            {
                return (configuration, storedCertificate, privateKey);
            }
        }

        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest($"CN={request.RelayHostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(request.RelayHostname);
        if (!network.SubmissionHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            Uri.CheckHostName(network.SubmissionHost) == UriHostNameType.Dns)
        {
            san.AddDnsName(network.SubmissionHost);
        }
        foreach (var address in network.BindAddresses.Select(IPAddress.Parse).Where(item => !IPAddress.IsLoopback(item)))
        {
            san.AddIpAddress(address);
        }
        certificateRequest.CertificateExtensions.Add(san.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var certificatePem = certificate.ExportCertificatePem();
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var certificateReference = await secretStore.SaveAsync("mail-relay-tls-certificate", certificatePem, cancellationToken);
        var keyReference = await secretStore.SaveAsync("mail-relay-tls-private-key", privateKeyPem, cancellationToken);
        return (configuration with { TlsCertificateSecretReference = certificateReference, TlsPrivateKeySecretReference = keyReference }, certificatePem, privateKeyPem);
    }

    private async Task<string> EnsureMailRuntimeAsync(CancellationToken cancellationToken)
    {
        var required = new[] { "postfix", "opendkim", "saslpasswd2" };
        var missing = new List<string>();
        foreach (var binary in required)
        {
            if (!await CommandExistsAsync(binary, cancellationToken))
            {
                missing.Add(binary);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Mail Relay requires {string.Join(", ", missing)} on this Home Assistant add-on host. These packages are provided by the LMS Edge Gateway add-on image.");
        }

        return "Postfix, OpenDKIM and SASL are present on this host.";
    }

    private async Task<(MailRelayDomain Domain, string PrivateKeyPem, string PublicDnsValue)> EnsureDkimMaterialAsync(
        Guid configurationId,
        MailRelayDomain? existing,
        MailRelaySetupRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (existing is not null &&
            existing.CurrentDkimSelector.Equals(request.DkimSelector, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(existing.CurrentDkimPrivateKeySecretReference))
        {
            var privateKey = await secretStore.ResolveAsync(existing.CurrentDkimPrivateKeySecretReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(privateKey))
            {
                using var loaded = RSA.Create();
                loaded.ImportFromPem(privateKey);
                return (existing, privateKey, BuildDkimDnsValue(loaded));
            }
        }

        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportRSAPrivateKeyPem();
        var reference = await secretStore.SaveAsync(
            $"mail-relay-dkim-{request.SendingDomain}-{request.DkimSelector}-{Guid.NewGuid():N}",
            privateKeyPem,
            cancellationToken);
        var rotatesExistingKey = existing is not null &&
                                 !string.IsNullOrWhiteSpace(existing.CurrentDkimPrivateKeySecretReference);
        var domain = existing is null
            ? new MailRelayDomain(
                Guid.NewGuid(), configurationId, request.CloudflareZoneId, request.SendingDomain, true, request.DkimSelector, reference, now, now,
                null, null, null, null, null, null, null, null,
                MailRelayDnsStatus.Pending, MailRelayDnsStatus.Pending, MailRelayDnsStatus.Pending,
                MailRelayDmarcPolicy.Monitor, null, now, now)
            : existing with
            {
                Enabled = true,
                PreviousDkimSelector = rotatesExistingKey ? existing.CurrentDkimSelector : existing.PreviousDkimSelector,
                PreviousDkimPrivateKeySecretReference = rotatesExistingKey ? existing.CurrentDkimPrivateKeySecretReference : existing.PreviousDkimPrivateKeySecretReference,
                PreviousDkimCreatedUtc = rotatesExistingKey ? existing.CurrentDkimCreatedUtc : existing.PreviousDkimCreatedUtc,
                PreviousDkimActivatedUtc = rotatesExistingKey ? existing.CurrentDkimActivatedUtc : existing.PreviousDkimActivatedUtc,
                CurrentDkimSelector = request.DkimSelector,
                CurrentDkimPrivateKeySecretReference = reference,
                CurrentDkimCreatedUtc = now,
                CurrentDkimActivatedUtc = now,
                UpdatedUtc = now
            };
        return (domain, privateKeyPem, BuildDkimDnsValue(rsa));
    }

    private static MailRelayDomain CreateDraftDomain(
        Guid configurationId,
        MailRelaySetupRequest request,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            configurationId,
            request.CloudflareZoneId,
            request.SendingDomain,
            false,
            request.DkimSelector,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            MailRelayDnsStatus.NotChecked,
            MailRelayDnsStatus.NotChecked,
            MailRelayDnsStatus.NotChecked,
            MailRelayDmarcPolicy.Monitor,
            null,
            now,
            now);

    private static string BuildDkimDnsValue(RSA rsa) =>
        $"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}";

    private async Task<string> GetCloudflareAsync(CancellationToken cancellationToken)
    {
        var token = await cloudflareTokenStore.GetTokenAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("The Edge Gateway Cloudflare token is not available.")
            : token;
    }

    private async Task<CloudflareDnsRecord> UpsertRecordAsync(
        string token,
        string zoneId,
        IReadOnlyList<CloudflareDnsRecord> records,
        string type,
        string name,
        string content,
        bool proxied,
        CancellationToken cancellationToken)
    {
        var existing = Find(records, type, name).FirstOrDefault(item =>
            !type.Equals("TXT", StringComparison.OrdinalIgnoreCase) ||
            PurposeMatches(item.Content, content));
        var proposed = existing is null
            ? new CloudflareDnsRecord(string.Empty, zoneId, name, type, content, proxied, 1, ManagedDnsComment, null)
            : existing with
            {
                Name = name,
                Type = type,
                Content = content,
                Proxied = proxied
            };
        if (existing is null)
        {
            return await cloudflareDnsService.CreateRecordAsync(token, zoneId, proposed, cancellationToken);
        }

        if (existing.Content.Equals(content, StringComparison.Ordinal) && existing.Proxied == proxied)
        {
            return existing;
        }

        return await cloudflareDnsService.UpdateRecordAsync(token, zoneId, proposed, cancellationToken);
    }

    private static bool PurposeMatches(string existing, string proposed) =>
        existing.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) == proposed.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) &&
        existing.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase) == proposed.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase) &&
        existing.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase) == proposed.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase);

    private static void ValidatePreservedMailDns(
        IReadOnlyList<CloudflareDnsRecord> before,
        IReadOnlyList<CloudflareDnsRecord> after,
        string domain,
        string expectedSpf,
        string publicIp,
        string dkimName,
        string expectedDkim,
        string dmarcName,
        string expectedDmarc)
    {
        var beforeMx = Find(before, "MX", domain).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var afterMx = Find(after, "MX", domain).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (!beforeMx.Select(MailDnsIdentity).SequenceEqual(afterMx.Select(MailDnsIdentity), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Existing MX records changed while applying Mail Relay DNS. Setup stopped rather than accepting that result.");
        }

        var providerDkimBefore = before
            .Where(item => IsDkimRecordForDomain(item, domain) && !item.Name.TrimEnd('.').Equals(dkimName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(MailDnsIdentity)
            .ToArray();
        var providerDkimAfter = after
            .Where(item => IsDkimRecordForDomain(item, domain) && !item.Name.TrimEnd('.').Equals(dkimName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(MailDnsIdentity)
            .ToArray();
        if (!providerDkimBefore.SequenceEqual(providerDkimAfter, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("An existing provider DKIM selector changed while applying Mail Relay DNS. Setup stopped.");
        }

        var spfRecords = Find(after, "TXT", domain)
            .Where(item => item.Content.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (spfRecords.Length != 1 || !spfRecords[0].Content.Equals(expectedSpf, StringComparison.Ordinal) ||
            !spfRecords[0].Content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(term => SpfTermAuthorizesIpv4(term, publicIp)))
        {
            throw new InvalidOperationException("The resulting SPF record is not the single reviewed value authorising both the existing provider and LMS.");
        }
        if (!Find(after, "TXT", dkimName).Any(item => item.Content.Equals(expectedDkim, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The LMS DKIM record does not match the generated signing key.");
        }
        if (!Find(after, "TXT", dmarcName).Any(item => item.Content.Equals(expectedDmarc, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The existing DMARC policy was not preserved exactly.");
        }
    }

    private async Task<bool> PublicTxtMatchesAsync(
        string name,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        var result = await QueryPublicDnsAsync("TXT", name, cancellationToken);
        if (result is null)
        {
            return false;
        }

        return result
            .Select(NormalizeDigTxt)
            .Any(value => value.Equals(expectedValue, StringComparison.Ordinal));
    }

    private async Task<bool> PublicAddressMatchesAsync(
        string name,
        string expectedAddress,
        CancellationToken cancellationToken)
    {
        var result = await QueryPublicDnsAsync("A", name, cancellationToken);
        return result is not null &&
               result.Any(value => value.Equals(expectedAddress, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<string>?> QueryPublicDnsAsync(
        string type,
        string name,
        CancellationToken cancellationToken)
    {
        var result = await hostCommand.RunAsync(
            "dig",
            ["+short", type, name, "@1.1.1.1"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(15));
        if (IsMissingHostCommand(result))
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<MailRelayPublicIpDnsCheck> InspectPtrAlignmentAsync(
        string publicIp,
        string relayHostname,
        CancellationToken cancellationToken)
    {
        try
        {
            var address = IPAddress.Parse(publicIp);
            var reverse = await Dns.GetHostEntryAsync(address).WaitAsync(cancellationToken);
            var ptrHostname = reverse.HostName.Trim().TrimEnd('.');
            var forward = await Dns.GetHostAddressesAsync(ptrHostname, cancellationToken);
            var aligned = ptrHostname.Equals(relayHostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
                          forward.Any(candidate => candidate.Equals(address));
            return new MailRelayPublicIpDnsCheck(
                "PTR / reverse DNS",
                publicIp,
                aligned ? MailRelayDnsStatus.Pass : MailRelayDnsStatus.Warning,
                false,
                aligned
                    ? $"{publicIp} and {relayHostname} resolve back to each other."
                    : $"PTR currently resolves to {ptrHostname}. Set the provider-managed PTR to {relayHostname}; Cloudflare cannot change reverse DNS.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new MailRelayPublicIpDnsCheck(
                "PTR / reverse DNS",
                publicIp,
                MailRelayDnsStatus.Warning,
                false,
                $"No matching PTR was found. Set the provider-managed PTR for {publicIp} to {relayHostname}; Cloudflare cannot change reverse DNS.");
        }
    }

    private static string NormalizeDigTxt(string value) => value
        .Replace("\" \"", string.Empty, StringComparison.Ordinal)
        .Trim()
        .Trim('"');

    private static bool IsDkimRecordForDomain(CloudflareDnsRecord record, string domain) =>
        record.Name.Contains("._domainkey.", StringComparison.OrdinalIgnoreCase) &&
        record.Name.TrimEnd('.').EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase) &&
        (record.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) || record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase));

    private static string MailDnsIdentity(CloudflareDnsRecord record) =>
        $"{record.Id}\n{record.Type}\n{record.Name}\n{record.Content}";

    private async Task SaveDnsOwnershipAsync(
        Guid domainId,
        CloudflareDnsRecord record,
        string purpose,
        IReadOnlyList<CloudflareDnsRecord> recordsBefore,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var trackedRecords = await store.ListDnsRecordsAsync(domainId, cancellationToken);
        var existingTracking = trackedRecords
            .FirstOrDefault(item => item.CloudflareRecordId.Equals(record.Id, StringComparison.Ordinal))
            ?? trackedRecords.FirstOrDefault(item =>
                item.Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase) &&
                item.Type.Equals(record.Type, StringComparison.OrdinalIgnoreCase) &&
                item.Name.TrimEnd('.').Equals(record.Name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        var originalRecord = recordsBefore.FirstOrDefault(item => item.Id.Equals(record.Id, StringComparison.Ordinal));
        var createdByLms = existingTracking?.CreatedByLms ?? originalRecord is null;
        var modifiedByLms = existingTracking?.ModifiedByLms == true ||
                            originalRecord is not null && !originalRecord.Content.Equals(record.Content, StringComparison.Ordinal);
        var changeType = createdByLms
            ? MailRelayDnsChangeType.Created
            : modifiedByLms
                ? MailRelayDnsChangeType.ModifiedShared
                : MailRelayDnsChangeType.ObservedExisting;
        var tracking = existingTracking is null
            ? new MailRelayDnsRecord(
                Guid.NewGuid(),
                domainId,
                record.Id,
                record.Type,
                record.Name,
                purpose,
                createdByLms,
                modifiedByLms,
                originalRecord?.Content,
                record.Content,
                changeType,
                now,
                now)
            : existingTracking with
            {
                CloudflareRecordId = record.Id,
                Type = record.Type,
                Name = record.Name,
                Purpose = purpose,
                ModifiedByLms = modifiedByLms,
                OriginalValue = existingTracking.OriginalValue ?? originalRecord?.Content,
                CurrentValue = record.Content,
                ChangeType = changeType,
                UpdatedUtc = now
            };
        await store.SaveDnsRecordAsync(tracking, cancellationToken);
    }

    private async Task<(IReadOnlyList<string> BindAddresses, string SubmissionHost)> InspectPrivateNetworkAsync(
        bool allowTailscale,
        bool allowTrustedLan,
        CancellationToken cancellationToken)
    {
        _ = allowTailscale;
        var addresses = new List<string> { "127.0.0.1" };
        var submissionHost = "localhost";
        if (allowTrustedLan)
        {
            var lanAddresses = await ReadPrivateLanAddressesAsync(cancellationToken);
            addresses.AddRange(lanAddresses);
            if (lanAddresses.Count > 0)
            {
                submissionHost = lanAddresses[0];
            }
        }

        return (addresses.Distinct(StringComparer.Ordinal).ToArray(), submissionHost);
    }

    private async Task<IReadOnlyList<string>> ReadPrivateLanAddressesAsync(CancellationToken cancellationToken)
    {
        var result = await hostCommand.RunAsync(
            "hostname",
            ["-I"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(10));
        if (result.Succeeded)
        {
            var fromHostname = result.StandardOutput
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && IsPrivateLan(ip))
                .ToArray();
            if (fromHostname.Length > 0)
            {
                return fromHostname;
            }
        }

        return (await ReadLocalIpv4AddressesAsync(cancellationToken))
            .Where(value => IPAddress.TryParse(value, out var ip) && !IPAddress.IsLoopback(ip) && IsPrivateLan(ip))
            .ToArray();
    }

    private static bool IsPrivateLan(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static MailRelayLegacySubmissionRequest NormalizeLegacyRequest(MailRelayLegacySubmissionRequest request)
    {
        if (!request.Enabled)
        {
            return new(false, [], []);
        }

        if (request.AllowedNetworks.Count is < 1 or > 128)
        {
            throw new InvalidOperationException("Add between 1 and 128 allowed device IP addresses or CIDR networks.");
        }

        var allowedNetworks = request.AllowedNetworks
            .Select(NormalizeLegacyAllowedNetwork)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(true, [], allowedNetworks);
    }

    private static string NormalizeLegacyAllowedNetwork(string value)
    {
        var normalized = NormalizeLegacyIpv4Network(value, requireSingleAddress: false, out var network, out var prefix);
        if (prefix == 0 || network >= 0xE0000000)
        {
            throw new InvalidOperationException($"'{value?.Trim()}' is not a safe SMTP source network. Allow explicit device addresses or bounded private, Tailscale, or public CIDRs; never all sources.");
        }
        if (!IsInternalNetwork(network, prefix) && prefix < 24)
        {
            throw new InvalidOperationException($"Public SMTP source range '{value?.Trim()}' is too broad. Use an exact address or a /24-or-narrower CIDR, ideally with matching UFW or upstream firewall filtering.");
        }

        return normalized;
    }

    private static string NormalizeLegacyIpv4Network(
        string value,
        bool requireSingleAddress,
        out uint network,
        out int prefix)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        var parts = trimmed.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"'{trimmed}' is not a valid IPv4 address or network.");
        }
        if (requireSingleAddress && parts.Length != 1)
        {
            throw new InvalidOperationException($"Listen address '{trimmed}' must be one exact IPv4 address, not a network.");
        }

        prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
        {
            throw new InvalidOperationException($"'{trimmed}' has an invalid IPv4 CIDR prefix.");
        }

        var numeric = ToUInt32(address);
        var mask = prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);
        network = numeric & mask;

        var normalizedAddress = FromUInt32(network).ToString();
        return prefix == 32 ? normalizedAddress : $"{normalizedAddress}/{prefix}";
    }

    private static bool IsInternalNetwork(uint network, int prefix) =>
        IsWithin(network, prefix, 0x0A000000, 8) ||
        IsWithin(network, prefix, 0xAC100000, 12) ||
        IsWithin(network, prefix, 0xC0A80000, 16) ||
        IsWithin(network, prefix, 0x64400000, 10) ||
        IsWithin(network, prefix, 0x7F000000, 8) ||
        IsWithin(network, prefix, 0xA9FE0000, 16);

    private static bool IsWithin(uint network, int prefix, uint allowedNetwork, int allowedPrefix)
    {
        if (prefix < allowedPrefix)
        {
            return false;
        }
        var mask = uint.MaxValue << (32 - allowedPrefix);
        return (network & mask) == allowedNetwork;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) => new(
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private async Task<IReadOnlyList<string>> ReadLocalIpv4AddressesAsync(CancellationToken cancellationToken)
    {
        var addresses = new List<string> { "127.0.0.1" };
        var result = await hostCommand.RunAsync(
            "ip",
            ["-o", "-4", "address", "show"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(10));
        if (result.Succeeded)
        {
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var inetIndex = Array.IndexOf(fields, "inet");
                if (inetIndex >= 0 && inetIndex + 1 < fields.Length &&
                    IPAddress.TryParse(fields[inetIndex + 1].Split('/')[0], out var address) &&
                    address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(address.ToString());
                }
            }
        }

        return addresses.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private async Task InstallRuntimePolicyAsync(
        MailRelayConfiguration configuration,
        IReadOnlyList<string> submissionAddresses,
        IReadOnlyList<MailRelayDomain> domains,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        await WriteTextFileAsync(Path.Combine(paths.ConfigDirectory, "main.cf"), BuildMainCf(configuration), cancellationToken);
        DeleteSmarthostPasswordFile(Path.Combine(paths.ConfigDirectory, "sasl_passwd"));
        await WriteTextFileAsync(Path.Combine(paths.ConfigDirectory, "master-lms.cf"), BuildMasterCf(submissionAddresses, configuration), cancellationToken);
        await WriteTextFileAsync(Path.Combine(paths.ConfigDirectory, "legacy_clients.cidr"), BuildLegacyClientAccess(configuration.EffectiveLegacyAllowedNetworks), cancellationToken);
        await WriteTextFileAsync(Path.Combine(paths.ConfigDirectory, "legacy_senders.pcre"), BuildLegacySenderAccess(domains), cancellationToken);
        await WriteTextFileAsync(Path.Combine(paths.ConfigDirectory, "header_checks.pcre"), BuildHeaderChecks(domains), cancellationToken);
        RestrictConfigurationAccess();
    }

    private async Task WriteConfigurationFilesAsync(
        MailRelayConfiguration configuration,
        IReadOnlyList<string> bindAddresses,
        string certificatePem,
        string tlsKeyPem,
        IReadOnlyList<MailRelayDomain> domains,
        IReadOnlyList<MailRelayClient> clients,
        IReadOnlyDictionary<Guid, string> dkimKeys,
        CancellationToken cancellationToken)
    {
        var root = paths.ConfigDirectory;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "opendkim"));
        Directory.CreateDirectory(Path.Combine(root, "tls"));

        var dkimRoot = Path.Combine(root, "dkim");
        if (Directory.Exists(dkimRoot))
        {
            Directory.Delete(dkimRoot, true);
        }

        await WriteTextFileAsync(Path.Combine(root, "main.cf"), BuildMainCf(configuration), cancellationToken);
        DeleteSmarthostPasswordFile(Path.Combine(root, "sasl_passwd"));
        await WriteTextFileAsync(Path.Combine(root, "master-lms.cf"), BuildMasterCf(bindAddresses, configuration), cancellationToken);
        await WriteTextFileAsync(
            Path.Combine(root, "smtpd.conf"),
            "pwcheck_method: auxprop\nauxprop_plugin: sasldb\nmech_list: PLAIN LOGIN\nsasldb_path: /var/lib/lms/sasldb2\n",
            cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "sender_login_maps"), BuildPostfixSenderLoginMaps(clients, configuration.RelayHostname), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "legacy_clients.cidr"), BuildLegacyClientAccess(configuration.EffectiveLegacyAllowedNetworks), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "legacy_senders.pcre"), BuildLegacySenderAccess(domains), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "header_checks.pcre"), BuildHeaderChecks(domains), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "opendkim.conf"), BuildOpenDkimConfig(), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "opendkim", "KeyTable"), BuildKeyTable(domains), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "opendkim", "SigningTable"), BuildSigningTable(domains), cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "opendkim", "TrustedHosts"), "127.0.0.1\nlocalhost\n", cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "tls", "relay.crt"), certificatePem, cancellationToken);
        await WriteTextFileAsync(Path.Combine(root, "tls", "relay.key"), tlsKeyPem, cancellationToken);

        foreach (var domain in domains.Where(item => item.Enabled))
        {
            var keyPath = Path.Combine(root, "dkim", domain.DomainName, $"{domain.CurrentDkimSelector}.private");
            await WriteTextFileAsync(keyPath, dkimKeys[domain.Id], cancellationToken);
        }
    }

    private static async Task WriteTextFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var text = content.EndsWith('\n') ? content : content + "\n";
        await File.WriteAllTextAsync(path, text, cancellationToken);
    }

    private void RestrictConfigurationAccess()
    {
        if (!Directory.Exists(paths.ConfigDirectory))
        {
            return;
        }

        TrySetUnixMode(paths.ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(paths.ConfigDirectory, "*", SearchOption.AllDirectories))
        {
            TrySetUnixMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TrySetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private string BuildMainCf(MailRelayConfiguration configuration) => $$"""
        # Managed by Linux Made Sane
        # Do not edit manually
        compatibility_level = 3.11
        myhostname = {{configuration.RelayHostname}}
        maillog_file_prefixes = /var, /dev/stdout, /data
        maillog_file = {{paths.MailLogPath}}
        myorigin = $myhostname
        mydestination = localhost
        relay_domains =
        inet_interfaces = all
        inet_protocols = ipv4
        mynetworks = 127.0.0.0/8
        smtpd_banner = $myhostname ESMTP LMS Mail Relay
        biff = no
        append_dot_mydomain = no
        readme_directory = no
        smtpd_tls_cert_file = /lms-config/tls/relay.crt
        smtpd_tls_key_file = /lms-config/tls/relay.key
        smtpd_tls_security_level = may
        smtpd_tls_auth_only = yes
        smtp_tls_security_level = may
        smtp_tls_mandatory_protocols = >=TLSv1.2
        smtp_tls_mandatory_ciphers = high
        smtp_tcp_port = smtp
        smtp_connect_timeout = 15
        smtp_helo_timeout = 15
        relayhost =
        default_transport = smtp
        smtp_sasl_auth_enable = no
        smtp_tls_wrappermode = no
        smtpd_sasl_auth_enable = yes
        smtpd_sasl_type = cyrus
        smtpd_sasl_path = smtpd
        smtpd_sasl_local_domain = $myhostname
        smtpd_sasl_security_options = noanonymous
        broken_sasl_auth_clients = yes
        smtpd_sender_login_maps = pcre:/etc/postfix/sender_login_maps
        smtpd_sender_restrictions = reject_authenticated_sender_login_mismatch
        header_checks = pcre:/etc/postfix/header_checks.pcre
        smtpd_relay_restrictions = permit_sasl_authenticated, reject_unauth_destination
        smtpd_recipient_restrictions = permit_sasl_authenticated, reject
        smtpd_client_connection_rate_limit = 100
        smtpd_client_message_rate_limit = 100
        smtpd_milters = inet:127.0.0.1:8891
        non_smtpd_milters = inet:127.0.0.1:8891
        milter_protocol = 6
        milter_default_action = tempfail
        mailbox_size_limit = 0
        message_size_limit = 26214400
        recipient_delimiter = +
        """;

    private static void DeleteSmarthostPasswordFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static string BuildMasterCf(
        IReadOnlyList<string> submissionAddresses,
        MailRelayConfiguration configuration)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane");
        builder.AppendLine("# Do not edit manually");
        foreach (var address in submissionAddresses.Distinct(StringComparer.Ordinal))
        {
            builder.AppendLine($"{address}:587 inet n - n - - smtpd");
            builder.AppendLine("  -o syslog_name=postfix/submission");
            builder.AppendLine("  -o smtpd_tls_security_level=encrypt");
            builder.AppendLine("  -o smtpd_sasl_auth_enable=yes");
            builder.AppendLine("  -o smtpd_client_restrictions=");
            builder.AppendLine("  -o smtpd_sender_restrictions=reject_authenticated_sender_login_mismatch");
            builder.AppendLine("  -o smtpd_relay_restrictions=permit_sasl_authenticated,reject");
            builder.AppendLine("  -o smtpd_recipient_restrictions=permit_sasl_authenticated,reject");
        }

        if (configuration.AllowLegacyPort25)
        {
            var networks = string.Join(',', configuration.EffectiveLegacyAllowedNetworks);
            builder.AppendLine("25 inet n - n - 20 smtpd");
            builder.AppendLine("  -o syslog_name=postfix/legacy");
            builder.AppendLine("  -o smtpd_tls_security_level=none");
            builder.AppendLine("  -o smtpd_tls_auth_only=no");
            builder.AppendLine("  -o smtpd_sasl_auth_enable=no");
            builder.AppendLine($"  -o mynetworks={networks}");
            builder.AppendLine("  -o smtpd_client_restrictions=check_client_access,cidr:/etc/postfix/legacy_clients.cidr,reject");
            builder.AppendLine("  -o smtpd_sender_restrictions=check_sender_access,pcre:/etc/postfix/legacy_senders.pcre,reject");
            builder.AppendLine("  -o smtpd_relay_restrictions=permit_mynetworks,reject");
            builder.AppendLine("  -o smtpd_recipient_restrictions=permit_mynetworks,reject");
        }

        return builder.ToString();
    }

    internal static string BuildLegacyClientAccess(IEnumerable<string> allowedNetworks) =>
        string.Join('\n', allowedNetworks.Select(network => $"{network} OK")) +
        "\n0.0.0.0/0 REJECT Legacy SMTP source is not allowed\n";

    internal static string BuildLegacySenderAccess(IEnumerable<MailRelayDomain> domains)
    {
        var allowed = domains
            .Where(domain => domain.Enabled)
            .Select(domain => $"/^.+@{Regex.Escape(domain.DomainName)}$/i OK");
        return string.Join('\n', allowed) + "\n/.*/ REJECT Sender domain is not configured in LMS Mail Relay\n";
    }

    internal static string BuildHeaderChecks(IEnumerable<MailRelayDomain> domains)
    {
        var allowed = domains
            .Where(domain => domain.Enabled)
            .SelectMany(domain => new[]
            {
                $"/^From:[^<\\r\\n]*<[^<>@\\r\\n]+@{Regex.Escape(domain.DomainName)}>[[:space:]]*$/i DUNNO",
                $"/^From:[[:space:]]*[^<>,@[:space:]]+@{Regex.Escape(domain.DomainName)}[[:space:]]*$/i DUNNO"
            });
        return string.Join('\n', allowed) + "\n/^From:/i REJECT Header From domain is not configured for LMS DKIM signing\n";
    }

    private static string BuildOpenDkimConfig() => """
        # Managed by Linux Made Sane
        Syslog yes
        UMask 007
        Mode sv
        Canonicalization relaxed/simple
        OversignHeaders From
        UserID opendkim
        Socket inet:8891@127.0.0.1
        KeyTable refile:/run/opendkim/KeyTable
        SigningTable refile:/run/opendkim/SigningTable
        ExternalIgnoreList refile:/run/opendkim/TrustedHosts
        InternalHosts refile:/run/opendkim/TrustedHosts
        """;

    internal static string BuildPostfixSenderLoginMaps(IEnumerable<MailRelayClient> clients, string relayHostname)
    {
        var ownersByDomain = clients
            .Where(item => item.Enabled)
            .SelectMany(client => client.AllowedSenderDomains.Select(domain => new { Domain = domain, client.Username }))
            .GroupBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var owners = group
                    .SelectMany(item => new[] { item.Username, $"{item.Username}@{relayHostname}" })
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                return $"/^.+@{Regex.Escape(group.Key)}$/i {string.Join(", ", owners)}";
            });
        return string.Join('\n', ownersByDomain) + "\n/.*/ __lms_sender_not_authorised__\n";
    }

    private static string BuildKeyTable(IEnumerable<MailRelayDomain> domains) => string.Join('\n',
        domains.Where(item => item.Enabled).Select(domain =>
            $"{domain.CurrentDkimSelector}._domainkey.{domain.DomainName} {domain.DomainName}:{domain.CurrentDkimSelector}:/run/opendkim/keys/{domain.DomainName}/{domain.CurrentDkimSelector}.private")) + "\n";

    private static string BuildSigningTable(IEnumerable<MailRelayDomain> domains) => string.Join('\n',
        domains.Where(item => item.Enabled).Select(domain =>
            $"*@{domain.DomainName} {domain.CurrentDkimSelector}._domainkey.{domain.DomainName}")) + "\n";

    private async Task DeleteSecretIfPresentAsync(string? secretReference, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(secretReference))
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
        }
    }

    private async Task RemoveManagedDomainDnsAsync(
        string apiToken,
        MailRelayConfiguration configuration,
        MailRelayDomain domain,
        ICollection<string> changes,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var trackedRecords = await store.ListDnsRecordsAsync(domain.Id, cancellationToken);
        if (trackedRecords.Count == 0)
        {
            if (domain.Enabled)
            {
                warnings.Add($"No LMS DNS ownership records exist for {domain.DomainName}; its DNS was left unchanged.");
            }
            return;
        }

        var currentRecords = await cloudflareDnsService.ListRecordsAsync(apiToken, domain.CloudflareZoneId, cancellationToken);
        foreach (var tracked in trackedRecords)
        {
            var current = currentRecords.FirstOrDefault(record =>
                record.Id.Equals(tracked.CloudflareRecordId, StringComparison.Ordinal));
            if (current is null)
            {
                changes.Add($"{tracked.Name}: already absent");
                continue;
            }

            if (tracked.Purpose.Equals("SPF", StringComparison.OrdinalIgnoreCase) &&
                (tracked.CreatedByLms || tracked.ModifiedByLms))
            {
                var currentSpfRecords = Find(currentRecords, "TXT", tracked.Name)
                    .Where(record => record.Content.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (currentSpfRecords.Length != 1 || !currentSpfRecords[0].Id.Equals(current.Id, StringComparison.Ordinal))
                {
                    warnings.Add($"{tracked.Name}: SPF now has conflicting records, so LMS left it unchanged.");
                    continue;
                }

                if (tracked.CreatedByLms && current.Content.Equals(tracked.CurrentValue, StringComparison.Ordinal))
                {
                    await cloudflareDnsService.DeleteRecordAsync(apiToken, domain.CloudflareZoneId, current.Id, cancellationToken);
                    changes.Add($"{tracked.Name}: removed the unchanged LMS-created SPF record");
                    continue;
                }

                var withoutLms = RemoveLmsSpfIpv4Authorization(current.Content, configuration.PublicIpAddress);
                if (withoutLms.Equals(current.Content, StringComparison.Ordinal))
                {
                    changes.Add($"{tracked.Name}: LMS SPF authorisation was already absent");
                    continue;
                }
                if (!IsValidSpfValue(withoutLms, out var spfError))
                {
                    warnings.Add($"{tracked.Name}: removing the LMS IP would leave invalid SPF ({spfError}), so LMS preserved the current record.");
                    continue;
                }

                var updated = await cloudflareDnsService.UpdateRecordAsync(
                    apiToken,
                    domain.CloudflareZoneId,
                    current with { Content = withoutLms },
                    cancellationToken);
                if (!updated.Content.Equals(withoutLms, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Cloudflare did not return the expected SPF value for {tracked.Name}.");
                }
                changes.Add($"{tracked.Name}: removed only ip4:{configuration.PublicIpAddress}; all current provider mechanisms remain");
                continue;
            }

            if (!tracked.CreatedByLms)
            {
                changes.Add($"{tracked.Name}: preserved existing external record");
                continue;
            }

            var isStillExactLmsValue = current.Type.Equals(tracked.Type, StringComparison.OrdinalIgnoreCase) &&
                                       current.Name.TrimEnd('.').Equals(tracked.Name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
                                       current.Content.Equals(tracked.CurrentValue, StringComparison.Ordinal);
            if (!isStillExactLmsValue)
            {
                warnings.Add($"{tracked.Name}: its value changed after LMS created it, so LMS preserved it.");
                continue;
            }

            await cloudflareDnsService.DeleteRecordAsync(apiToken, domain.CloudflareZoneId, current.Id, cancellationToken);
            changes.Add($"{tracked.Name}: removed unchanged LMS-created {tracked.Purpose} record");
        }
    }

    internal static string RemoveLmsSpfIpv4Authorization(string currentValue, string publicIp)
    {
        var terms = currentValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', terms.Where(term => !IsExactLmsSpfIpv4Term(term, publicIp)));
    }

    private static bool IsExactLmsSpfIpv4Term(string term, string publicIp)
    {
        if (term.StartsWith('-') || term.StartsWith('~') || term.StartsWith('?'))
        {
            return false;
        }
        var value = term.TrimStart('+');
        if (!value.StartsWith("ip4:", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(publicIp, out var expected))
        {
            return false;
        }
        var network = value[4..].Split('/', StringSplitOptions.TrimEntries);
        return network.Length is 1 or 2 &&
               IPAddress.TryParse(network[0], out var actual) &&
               actual.Equals(expected) &&
               (network.Length == 1 || network[1].Equals("32", StringComparison.Ordinal));
    }

    private static bool IsValidSpfValue(string value, out string error)
    {
        var terms = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length < 2 || !terms[0].Equals("v=spf1", StringComparison.OrdinalIgnoreCase))
        {
            error = "the record must contain v=spf1 and at least one policy term";
            return false;
        }

        var lookups = 0;
        foreach (var term in terms.Skip(1))
        {
            if (!TryValidateSpfTerm(term, out var usesDnsLookup))
            {
                error = $"'{term}' is not a valid supported SPF term";
                return false;
            }
            if (usesDnsLookup)
            {
                lookups++;
            }
        }
        if (lookups > 10)
        {
            error = $"the record has {lookups} direct DNS lookup terms";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task VerifyLegacyPolicyAsync(
        MailRelayConfiguration configuration,
        CancellationToken cancellationToken)
    {
        const string map = "cidr:/etc/postfix/legacy_clients.cidr";
        foreach (var network in configuration.EffectiveLegacyAllowedNetworks)
        {
            var probeAddress = network.Split('/')[0];
            var allowed = await RunRequiredAsync(
                "postmap",
                ["-q", probeAddress, map],
                "Test an allowed legacy SMTP source",
                cancellationToken,
                timeout: TimeSpan.FromSeconds(20));
            if (!allowed.StandardOutput.Trim().Equals("OK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The legacy source policy did not allow {network}.");
            }
        }

        var rejected = await RunRequiredAsync(
            "postmap",
            ["-q", "203.0.113.254", map],
            "Test a non-allowlisted legacy SMTP source",
            cancellationToken,
            timeout: TimeSpan.FromSeconds(20));
        if (!rejected.StandardOutput.Trim().StartsWith("REJECT", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The legacy source policy did not reject a non-allowlisted address.");
        }
    }

    private static async Task VerifyListenerAsync(string address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(address), 25, timeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Postfix did not listen on {address}:25.", exception);
        }
    }

    private static string BuildRuntimeSummary(
        IReadOnlyList<string> submissionAddresses,
        MailRelayConfiguration configuration) =>
        configuration.AllowLegacyPort25
            ? $"Authenticated TCP/587 is listening on {string.Join(", ", submissionAddresses)}. Restricted legacy TCP/25 is listening on all adapters and accepts only {string.Join(", ", configuration.EffectiveLegacyAllowedNetworks)}."
            : $"Authenticated SMTP submission is listening only on {string.Join(", ", submissionAddresses)}:587.";

    private async Task ApplyRuntimeConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.ApplyScriptPath))
        {
            throw new InvalidOperationException(
                "Mail Relay runs in the Home Assistant add-on image. The apply script /usr/local/bin/lms-mail-relay-apply was not found on this machine.");
        }

        await RunRequiredAsync(
            paths.ApplyScriptPath,
            [],
            "Apply Mail Relay configuration",
            cancellationToken,
            timeout: CommandTimeout);

        var start = await hostCommand.RunAsync(
            "postfix",
            ["start"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(20));
        if (!start.Succeeded &&
            !IsMissingHostCommand(start) &&
            !start.StandardError.Contains("already running", StringComparison.OrdinalIgnoreCase) &&
            !start.StandardOutput.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Postfix could not start: {FirstUsefulLine(start.StandardError, start.StandardOutput)}");
        }
    }

    private async Task EnsureSaslUserAsync(
        string relayHostname,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var databaseDirectory = Path.GetDirectoryName(paths.SaslDatabasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        await RunRequiredAsync(
            "saslpasswd2",
            ["-p", "-c", "-f", paths.SaslDatabasePath, "-u", relayHostname, username],
            "Create the Mail Relay application credential",
            cancellationToken,
            standardInput: Encoding.UTF8.GetBytes(password + "\n"),
            timeout: TimeSpan.FromSeconds(30));

        if (File.Exists(paths.SaslDatabasePath))
        {
            TrySetUnixMode(paths.SaslDatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await hostCommand.RunAsync(
                "chmod",
                ["0640", paths.SaslDatabasePath],
                cancellationToken,
                timeout: TimeSpan.FromSeconds(20));
        }
    }

    private async Task WaitForListenerAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var socket = new TcpClient();
                await socket.ConnectAsync(IPAddress.Loopback, 587, cancellationToken);
                return;
            }
            catch (SocketException)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException(await DescribeListenerFailureAsync(cancellationToken));
    }

    private async Task<string> DescribeListenerFailureAsync(CancellationToken cancellationToken)
    {
        var status = await hostCommand.RunAsync("postfix", ["status"], cancellationToken, timeout: TimeSpan.FromSeconds(10));
        var check = await hostCommand.RunAsync("postfix", ["check"], cancellationToken, timeout: TimeSpan.FromSeconds(15));
        var logTail = string.Empty;
        if (File.Exists(paths.MailLogPath))
        {
            var lines = (await File.ReadAllLinesAsync(paths.MailLogPath, cancellationToken)).TakeLast(8);
            logTail = string.Join(" ", lines.Select(line => line.Trim()).Where(line => line.Length > 0));
        }

        return "Mail Relay did not start listening on 127.0.0.1:587 after applying configuration. " +
               FirstUsefulLine(status.StandardError, status.StandardOutput, check.StandardError, check.StandardOutput, logTail);
    }

    private async Task TryStopMailRuntimeAsync(ICollection<string>? warnings, CancellationToken cancellationToken)
    {
        var result = await hostCommand.RunAsync(
            "postfix",
            ["stop"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(30));
        if (result.Succeeded || IsMissingHostCommand(result) || warnings is null)
        {
            return;
        }

        warnings.Add($"Postfix could not be stopped: {FirstUsefulLine(result.StandardError, result.StandardOutput)}");
    }

    private static async Task<bool> TestOpenRelayRejectedAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, 587, cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
        await ReadSmtpResponseAsync(reader, cancellationToken);
        await WriteSmtpAsync(stream, "EHLO lms-open-relay-test\r\n", cancellationToken);
        await ReadSmtpResponseAsync(reader, cancellationToken);
        await WriteSmtpAsync(stream, "MAIL FROM:<probe@invalid.example>\r\n", cancellationToken);
        var mailCode = await ReadSmtpResponseAsync(reader, cancellationToken);
        if (mailCode >= 400)
        {
            return true;
        }
        await WriteSmtpAsync(stream, "RCPT TO:<probe@gmail.com>\r\n", cancellationToken);
        return await ReadSmtpResponseAsync(reader, cancellationToken) >= 400;
    }

    private static async Task WriteSmtpAsync(Stream stream, string command, CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cancellationToken);

    private static async Task<int> ReadSmtpResponseAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("SMTP service closed the connection.");
            if (line.Length >= 4 && int.TryParse(line[..3], out var code) && line[3] == ' ')
            {
                return code;
            }
        }
    }

    private async Task<MailRelayHostCommandResult> RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken,
        byte[]? standardInput = null,
        TimeSpan? timeout = null)
    {
        var result = await hostCommand.RunAsync(
            fileName,
            arguments,
            cancellationToken,
            standardInput: standardInput,
            timeout: timeout ?? TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{description} failed: {FirstUsefulLine(result.StandardError, result.StandardOutput)}");
        }

        return result;
    }

    private async Task<bool> CommandExistsAsync(string fileName, CancellationToken cancellationToken)
    {
        var which = await hostCommand.RunAsync("which", [fileName], cancellationToken, timeout: TimeSpan.FromSeconds(10));
        if (which.Succeeded && which.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 0)
        {
            return true;
        }

        var command = await hostCommand.RunAsync(
            "/bin/sh",
            ["-c", $"command -v {fileName}"],
            cancellationToken,
            timeout: TimeSpan.FromSeconds(10));
        return command.Succeeded && !string.IsNullOrWhiteSpace(command.StandardOutput);
    }

    private static bool IsMissingHostCommand(MailRelayHostCommandResult result) =>
        result.ExitCode is 127 or 126 ||
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("Failed to start", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path, string label, ICollection<string> warnings)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{label} could not be removed: {exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string path, string label, ICollection<string> warnings)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{label} could not be removed: {exception.Message}");
        }
    }

    private static string FirstUsefulLine(params string[] values) => values
        .SelectMany(value => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .FirstOrDefault() ?? "No diagnostic output was returned.";

    private static MailRelaySetupChange BuildAddressChange(IReadOnlyList<CloudflareDnsRecord> records, string hostname, string ip)
    {
        var sameName = records.Where(item => item.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sameName.Any(item => item.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)))
        {
            return new("Relay hostname", "A", hostname, ip, MailRelaySetupChangeKind.Blocked, $"{hostname} already has a CNAME. Remove or rename it before installing Mail Relay.");
        }
        var existing = sameName.FirstOrDefault(item => item.Type.Equals("A", StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return new("Relay hostname", "A", hostname, ip, MailRelaySetupChangeKind.Create, "Create a DNS-only A record. Cloudflare proxying will be off.");
        }
        return existing.Content == ip && !existing.Proxied
            ? new("Relay hostname", "A", hostname, ip, MailRelaySetupChangeKind.Keep, "The existing DNS-only A record is already correct.")
            : new("Relay hostname", "A", hostname, ip, MailRelaySetupChangeKind.Update, $"Replace {existing.Content} and force Cloudflare proxying off.");
    }

    private static MailRelaySetupChange BuildSpfChange(string domain, SpfAnalysis analysis)
    {
        if (analysis.Errors.Count > 0)
        {
            return new(
                "SPF",
                "TXT",
                domain,
                analysis.ExistingValue ?? string.Empty,
                MailRelaySetupChangeKind.Blocked,
                $"Existing SPF is not safe to modify: {string.Join(" ", analysis.Errors)}");
        }
        if (analysis.ExistingValue is null)
        {
            return new("SPF", "TXT", domain, analysis.ProposedValue, MailRelaySetupChangeKind.Create, "Create one strict SPF record authorising this relay IP.");
        }
        return analysis.ExistingValue.Equals(analysis.ProposedValue, StringComparison.OrdinalIgnoreCase)
            ? new("SPF", "TXT", domain, analysis.ProposedValue, MailRelaySetupChangeKind.Keep, "The existing SPF record already authorises this relay IP; all existing mechanisms remain unchanged.")
            : new("SPF", "TXT", domain, analysis.ProposedValue, MailRelaySetupChangeKind.Update, $"Merge the LMS IP into the existing shared SPF record. All mechanisms and the existing final policy are preserved ({analysis.DnsLookupTerms}/10 direct DNS lookup terms).");
    }

    private static SpfAnalysis AnalyzeSpf(
        IReadOnlyList<CloudflareDnsRecord> records,
        string domain,
        string publicIp)
    {
        var spfRecords = Find(records, "TXT", domain)
            .Where(item => item.Content.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (spfRecords.Length > 1)
        {
            return new(null, string.Empty, 0, [$"{domain} has multiple SPF records. Merge them into one record before continuing."]);
        }
        if (spfRecords.Length == 0)
        {
            return new(null, $"v=spf1 ip4:{publicIp} -all", 0, []);
        }

        var existing = spfRecords[0].Content.Trim();
        var tokens = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var errors = new List<string>();
        var lookupTerms = 0;
        if (tokens.Count == 0 || !tokens[0].Equals("v=spf1", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The record must start with v=spf1.");
        }

        foreach (var token in tokens.Skip(1))
        {
            if (!TryValidateSpfTerm(token, out var usesDnsLookup))
            {
                errors.Add($"'{token}' is not a valid supported SPF term.");
            }
            if (usesDnsLookup)
            {
                lookupTerms++;
            }
        }
        if (lookupTerms > 10)
        {
            errors.Add($"The record has {lookupTerms} direct DNS lookup terms; SPF permits at most 10.");
        }

        if (errors.Count > 0)
        {
            return new(existing, existing, lookupTerms, errors);
        }
        if (tokens.Skip(1).Any(token => SpfTermAuthorizesIpv4(token, publicIp)))
        {
            return new(existing, existing, lookupTerms, []);
        }

        tokens.Insert(1, $"ip4:{publicIp}");
        return new(existing, string.Join(' ', tokens), lookupTerms, []);
    }

    private static bool TryValidateSpfTerm(string token, out bool usesDnsLookup)
    {
        usesDnsLookup = false;
        var term = token.TrimStart('+', '-', '~', '?');
        if (term.Length == 0)
        {
            return false;
        }
        if (term.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (term.StartsWith("ip4:", StringComparison.OrdinalIgnoreCase))
        {
            return IsValidSpfNetwork(term[4..], AddressFamily.InterNetwork);
        }
        if (term.StartsWith("ip6:", StringComparison.OrdinalIgnoreCase))
        {
            return IsValidSpfNetwork(term[4..], AddressFamily.InterNetworkV6);
        }
        if (term.StartsWith("include:", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("exists:", StringComparison.OrdinalIgnoreCase))
        {
            usesDnsLookup = true;
            return term[(term.IndexOf(':') + 1)..].Length > 0;
        }
        if (term.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("a:", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("a/", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("mx", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("mx:", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("mx/", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("ptr", StringComparison.OrdinalIgnoreCase) ||
            term.StartsWith("ptr:", StringComparison.OrdinalIgnoreCase))
        {
            usesDnsLookup = true;
            return true;
        }
        if (term.StartsWith("redirect=", StringComparison.OrdinalIgnoreCase))
        {
            usesDnsLookup = true;
            return term.Length > "redirect=".Length;
        }
        if (term.StartsWith("exp=", StringComparison.OrdinalIgnoreCase))
        {
            return term.Length > "exp=".Length;
        }

        var equalsIndex = term.IndexOf('=');
        return equalsIndex > 0 && equalsIndex < term.Length - 1 &&
               term[..equalsIndex].All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    private static bool IsValidSpfNetwork(string value, AddressFamily family)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != family)
        {
            return false;
        }
        var maximumPrefix = family == AddressFamily.InterNetwork ? 32 : 128;
        return parts.Length == 1 || int.TryParse(parts[1], out var prefix) && prefix is >= 0 && prefix <= maximumPrefix;
    }

    private static bool SpfTermAuthorizesIpv4(string token, string publicIp)
    {
        if (token.StartsWith('-') || token.StartsWith('~') || token.StartsWith('?'))
        {
            return false;
        }
        var term = token.TrimStart('+');
        if (!term.StartsWith("ip4:", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(publicIp, out var candidate))
        {
            return false;
        }
        var networkParts = term[4..].Split('/', StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(networkParts[0], out var networkAddress) ||
            networkAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var prefix = networkParts.Length == 2 && int.TryParse(networkParts[1], out var parsedPrefix) ? parsedPrefix : 32;
        var mask = prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);
        return (ToUInt32(candidate) & mask) == (ToUInt32(networkAddress) & mask);
    }

    private static MailRelaySetupChange BuildDkimChange(
        IReadOnlyList<CloudflareDnsRecord> records,
        string domain,
        string selector,
        bool existingIsLmsManaged)
    {
        var name = $"{selector}._domainkey.{domain}";
        var existing = records.FirstOrDefault(item =>
            item.Name.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (item.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) || item.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)));
        return existing is null
            ? new("DKIM", "TXT", name, "RSA 2048-bit public key generated during install", MailRelaySetupChangeKind.Create, "Generate a private key in LMS secret storage and publish only its public key.")
            : existingIsLmsManaged && existing.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase)
                ? new("DKIM", existing.Type, name, "Existing LMS-managed selector", MailRelaySetupChangeKind.Keep, "Keep the existing LMS DKIM selector. Other provider selectors are untouched.")
                : new("DKIM", existing.Type, name, existing.Content, MailRelaySetupChangeKind.Blocked, $"{name} already belongs to another mail system. Choose a different LMS selector; the existing record will not be modified.");
    }

    private static MailRelaySetupChange BuildDmarcChange(IReadOnlyList<CloudflareDnsRecord> records, string domain)
    {
        var name = $"_dmarc.{domain}";
        var existing = Find(records, "TXT", name).Where(item => item.Content.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (existing.Length > 1)
        {
            return new("DMARC", "TXT", name, string.Empty, MailRelaySetupChangeKind.Blocked, $"{name} has multiple DMARC records. Resolve that conflict first.");
        }
        return existing.Length == 1
            ? new("DMARC", "TXT", name, existing[0].Content, MailRelaySetupChangeKind.Keep, "Keep the existing DMARC policy unchanged.")
            : new("DMARC", "TXT", name, "v=DMARC1; p=none", MailRelaySetupChangeKind.Create, "Create a monitoring policy. LMS will not invent a reporting mailbox.");
    }

    private static MailRelayExistingEmailConfiguration BuildExistingEmailConfiguration(
        IReadOnlyList<CloudflareDnsRecord> records,
        string domain,
        SpfAnalysis spf)
    {
        var mxRecords = Find(records, "MX", domain)
            .Select(item => item.Content.TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dkimRecords = records
            .Where(item => item.Name.Contains("._domainkey.", StringComparison.OrdinalIgnoreCase) &&
                           item.Name.TrimEnd('.').EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase) &&
                           (item.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) || item.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Name.TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dmarc = Find(records, "TXT", $"_dmarc.{domain}")
            .FirstOrDefault(item => item.Content.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
            ?.Content;
        var combined = string.Join(' ', mxRecords.Append(spf.ExistingValue ?? string.Empty));
        var provider = combined.Contains("mail.protection.outlook.com", StringComparison.OrdinalIgnoreCase) ||
                       combined.Contains("spf.protection.outlook.com", StringComparison.OrdinalIgnoreCase)
            ? MailRelayExistingProvider.Microsoft365
            : combined.Contains("aspmx.l.google.com", StringComparison.OrdinalIgnoreCase) ||
              combined.Contains("_spf.google.com", StringComparison.OrdinalIgnoreCase) ||
              mxRecords.Any(item => item.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase))
                ? MailRelayExistingProvider.GoogleWorkspace
                : mxRecords.Length > 0 || spf.ExistingValue is not null || dkimRecords.Length > 0 || dmarc is not null
                    ? MailRelayExistingProvider.ExistingMailProvider
                    : MailRelayExistingProvider.NoneDetected;

        return new(
            domain,
            provider,
            mxRecords,
            spf.ExistingValue,
            spf.ProposedValue,
            spf.DnsLookupTerms,
            dkimRecords,
            dmarc,
            ReadDmarcPolicy(dmarc),
            MailRelayDeliveryMode.DirectInternet);
    }

    private static string? ReadDmarcPolicy(string? dmarc)
    {
        if (string.IsNullOrWhiteSpace(dmarc))
        {
            return null;
        }
        return dmarc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.StartsWith("p=", StringComparison.OrdinalIgnoreCase))
            ?[2..]
            .Trim()
            .ToUpperInvariant();
    }

    private static IEnumerable<CloudflareDnsRecord> Find(IEnumerable<CloudflareDnsRecord> records, string type, string name) =>
        records.Where(item => item.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && item.Name.TrimEnd('.').Equals(name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

    private static bool PlansMatch(
        IReadOnlyList<MailRelaySetupChange> reviewed,
        IReadOnlyList<MailRelaySetupChange> current) =>
        reviewed.Count == current.Count && current.All(candidate => reviewed.Any(previous =>
            previous.Purpose.Equals(candidate.Purpose, StringComparison.Ordinal) &&
            previous.RecordType.Equals(candidate.RecordType, StringComparison.OrdinalIgnoreCase) &&
            previous.RecordName.Equals(candidate.RecordName, StringComparison.OrdinalIgnoreCase) &&
            previous.ProposedValue.Equals(candidate.ProposedValue, StringComparison.Ordinal) &&
            previous.Kind == candidate.Kind));

    private static MailRelaySetupRequest Normalize(MailRelaySetupRequest request) => request with
    {
        CloudflareZoneId = request.CloudflareZoneId.Trim(),
        RelayHostname = request.RelayHostname.Trim().TrimEnd('.').ToLowerInvariant(),
        SendingDomain = request.SendingDomain.Trim().TrimEnd('.').ToLowerInvariant(),
        DkimSelector = request.DkimSelector.Trim().ToLowerInvariant(),
        ApplicationName = request.ApplicationName.Trim(),
        ApplicationUsername = request.ApplicationUsername.Trim().ToLowerInvariant()
    };

    private static IEnumerable<string> Validate(MailRelaySetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CloudflareZoneId)) yield return "Choose a Cloudflare domain.";
        if (Uri.CheckHostName(request.RelayHostname) != UriHostNameType.Dns) yield return "Enter a valid relay hostname.";
        if (Uri.CheckHostName(request.SendingDomain) != UriHostNameType.Dns) yield return "Enter a valid sending domain.";
        if (!DnsLabelRegex().IsMatch(request.DkimSelector)) yield return "DKIM selector may contain letters, numbers and hyphens only.";
        if (string.IsNullOrWhiteSpace(request.ApplicationName) || request.ApplicationName.Length > 80) yield return "Enter an application name up to 80 characters.";
        if (!IsValidClientUsername(request.ApplicationUsername)) yield return "Application username must start with a letter or number and contain only letters, numbers, dots, underscores and hyphens.";
    }

    private static bool IsWithinZone(string value, string zone) =>
        !string.IsNullOrWhiteSpace(zone) && (value.Equals(zone, StringComparison.OrdinalIgnoreCase) || value.EndsWith('.' + zone, StringComparison.OrdinalIgnoreCase));

    internal static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%*-_";
        Span<char> chars = stackalloc char[28];
        for (var index = 0; index < chars.Length; index++) chars[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    internal static string HashCredentialPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal static bool IsValidClientUsername(string username) => UsernameRegex().IsMatch(username);

    private static List<MailRelaySetupProgressUpdate> CreateSteps() =>
    [
        new("preflight", "Check host and Cloudflare", MailRelaySetupStepState.Pending, string.Empty),
        new("draft", "Save relay draft", MailRelaySetupStepState.Pending, string.Empty),
        new("runtime", "Check mail runtime", MailRelaySetupStepState.Pending, string.Empty),
        new("keys", "Generate TLS and DKIM", MailRelaySetupStepState.Pending, string.Empty),
        new("apply", "Write Mail Relay configuration", MailRelaySetupStepState.Pending, string.Empty),
        new("config", "Install managed configuration", MailRelaySetupStepState.Pending, string.Empty),
        new("dns", "Configure DNS", MailRelaySetupStepState.Pending, string.Empty),
        new("start", "Start SMTP submission", MailRelaySetupStepState.Pending, string.Empty),
        new("security", "Test relay security", MailRelaySetupStepState.Pending, string.Empty),
        new("save", "Save LMS state", MailRelaySetupStepState.Pending, string.Empty)
    ];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DnsLabelRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    private sealed record SpfAnalysis(
        string? ExistingValue,
        string ProposedValue,
        int DnsLookupTerms,
        IReadOnlyList<string> Errors);
}
