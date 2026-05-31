using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayRelayProvisioningService(
    IOptions<EdgeGatewayCoreOptions> options,
    ICloudflareApiTokenStore tokenStore,
    ICloudflareZoneService zoneService,
    ICloudflareDnsService dnsService,
    ICloudflareTunnelService tunnelService,
    IEdgeGatewayConfigurationStore configurationStore,
    IProcessStatusProbe processStatusProbe) : IEdgeGatewayRelayProvisioningService
{
    private const string RelayNamespace = "ha-app-relay";
    private const string CloudflareTunnelTargetSuffix = ".cfargotunnel.com";
    private const string FallbackCaddyResponse = "No Linux Made Sane Edge Gateway route matched this hostname.";
    private const string HealthyTunnelStatus = "healthy";
    private const string CloudflaredProcessPattern = "(^|/)(cloudflared|lms-edge-cloudflared)( |$)";
    private const int TunnelValidationAttempts = 30;
    private static readonly TimeSpan TunnelValidationDelay = TimeSpan.FromSeconds(2);
    private static readonly HttpClient CloudflaredDownloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<EdgeGatewayRelayProvisioningResult> ProvisionRelayAsync(
        string domainName,
        bool replaceExistingDnsRecord = false,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var normalizedDomain = NormalizeDomainName(domainName);
            var relayHostname = BuildRelayHostname(normalizedDomain);
            var wildcardHostname = $"*.{relayHostname}";
            var caddyServiceUrl = ResolveCaddyServiceUrl();
            var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                return Failure("Cloudflare API token is not configured.", steps, warnings);
            }

            var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(zonesResult.Error))
            {
                return Failure(zonesResult.Error, steps, warnings);
            }

            var zone = zonesResult.Zones.FirstOrDefault(item =>
                item.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (zone is null)
            {
                return Failure($"The saved Cloudflare token cannot manage {normalizedDomain}.", steps, warnings);
            }

            if (string.IsNullOrWhiteSpace(zone.AccountId))
            {
                return Failure($"Cloudflare did not return an account id for {normalizedDomain}. Check the token account scope.", steps, warnings);
            }

            steps.Add($"Cloudflare zone found: {normalizedDomain} in {zone.AccountName}.");
            steps.Add($"Edge Gateway relay namespace: {relayHostname}.");

            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var existingRecords = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
            var existingWildcardRecord = existingRecords.FirstOrDefault(record => IsWildcardRecordForDomain(record, relayHostname));
            var tunnels = await tunnelService.ListTunnelsAsync(apiToken, zone.AccountId, cancellationToken);
            var tunnelBaseName = BuildTunnelBaseName(relayHostname);
            var tunnel = ResolveExistingTunnel(configuration, tunnels, tunnelBaseName);

            if (tunnel is null)
            {
                var tunnelName = BuildUniqueTunnelName(tunnelBaseName, tunnels);
                tunnel = await tunnelService.CreateTunnelAsync(apiToken, zone.AccountId, tunnelName, cancellationToken);
                steps.Add($"Created Cloudflare Tunnel {tunnel.Name}.");
            }
            else
            {
                steps.Add($"Reused Cloudflare Tunnel {tunnel.Name}.");
            }

            var dnsTarget = $"{tunnel.Id}{CloudflareTunnelTargetSuffix}";
            if (existingWildcardRecord is null)
            {
                await dnsService.CreateRecordAsync(
                    apiToken,
                    zone.Id,
                    new CloudflareDnsRecord(
                        string.Empty,
                        zone.Id,
                        wildcardHostname,
                        "CNAME",
                        dnsTarget,
                        true,
                        1,
                        options.Value.ManagedRecordComment,
                        null),
                    cancellationToken);
                steps.Add($"Created proxied wildcard DNS record {wildcardHostname} -> {dnsTarget}.");
            }
            else if (IsSameDnsTarget(existingWildcardRecord, dnsTarget))
            {
                steps.Add($"Wildcard DNS record already points at {dnsTarget}.");
            }
            else if (!replaceExistingDnsRecord)
            {
                warnings.Add($"Existing wildcard DNS record: {existingWildcardRecord.Type} {existingWildcardRecord.Name} -> {existingWildcardRecord.Content}.");
                return new EdgeGatewayRelayProvisioningResult(
                    false,
                    true,
                    null,
                    $"Wildcard DNS already exists for {relayHostname} and points at {existingWildcardRecord.Content}. Confirm replacement to point it at the LMS Edge Gateway tunnel.",
                    steps,
                    warnings);
            }
            else
            {
                await dnsService.DeleteRecordAsync(apiToken, zone.Id, existingWildcardRecord.Id, cancellationToken);
                await dnsService.CreateRecordAsync(
                    apiToken,
                    zone.Id,
                    new CloudflareDnsRecord(
                        string.Empty,
                        zone.Id,
                        wildcardHostname,
                        "CNAME",
                        dnsTarget,
                        true,
                        1,
                        options.Value.ManagedRecordComment,
                        null),
                    cancellationToken);
                steps.Add($"Replaced wildcard DNS record with {wildcardHostname} -> {dnsTarget}.");
            }

            var tunnelConfiguration = await tunnelService.GetConfigurationAsync(apiToken, zone.AccountId, tunnel.Id, cancellationToken);
            await tunnelService.UpdateConfigurationAsync(
                apiToken,
                zone.AccountId,
                tunnel.Id,
                new CloudflareTunnelConfiguration(MergeWildcardTunnelRoute(tunnelConfiguration.Routes, wildcardHostname, caddyServiceUrl)),
                cancellationToken);
            steps.Add($"Configured tunnel ingress {wildcardHostname} -> {caddyServiceUrl}.");

            var connectorToken = await tunnelService.GetTunnelTokenAsync(apiToken, zone.AccountId, tunnel.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(connectorToken))
            {
                return Failure("Cloudflare tunnel was created, but Cloudflare did not return a connector token.", steps, warnings);
            }

            await tokenStore.SaveTunnelTokenAsync(connectorToken, cancellationToken);
            steps.Add("Saved cloudflared connector token into the add-on options.");

            var cloudflaredRestart = await TryRestartCloudflaredAsync(cancellationToken);
            if (cloudflaredRestart.Success)
            {
                steps.Add(cloudflaredRestart.Message);
            }
            else if (!string.IsNullOrWhiteSpace(cloudflaredRestart.Message))
            {
                warnings.Add(cloudflaredRestart.Message);
            }

            var tunnelHealth = await WaitForTunnelHealthyAsync(
                apiToken,
                zone.AccountId,
                tunnel.Id,
                steps,
                warnings,
                cancellationToken);
            if (tunnelHealth.Success)
            {
                steps.Add(tunnelHealth.Summary);
            }
            else
            {
                warnings.Add(tunnelHealth.Summary);
            }

            var relay = new EdgeGatewayRelayZone(
                normalizedDomain,
                relayHostname,
                DateTimeOffset.UtcNow,
                wildcardHostname,
                dnsTarget,
                tunnel.Id,
                tunnelHealth.Tunnel?.Name ?? tunnel.Name,
                DateTimeOffset.UtcNow,
                tunnelHealth.Status,
                DateTimeOffset.UtcNow,
                tunnelHealth.Success ? string.Empty : tunnelHealth.Summary);

            var relayZones = configuration.RelayZones
                .Where(existing => !existing.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                .Append(relay)
                .OrderBy(existing => existing.DomainName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var updatedConfiguration = configuration with
            {
                RelayZones = relayZones,
                CloudflareTunnel = new CloudflareTunnelState(
                    tunnelHealth.Tunnel?.Name ?? tunnel.Name,
                    zone.AccountName,
                    tunnel.Id,
                    tunnelHealth.Success,
                    DateTimeOffset.UtcNow,
                    zone.AccountId)
            };

            var caddyResult = await ApplyCaddyConfigurationAsync(updatedConfiguration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);

            if (!tunnelHealth.Success)
            {
                return new EdgeGatewayRelayProvisioningResult(
                    false,
                    false,
                    relay,
                    $"{relayHostname} is configured, but Cloudflare reports the tunnel as {tunnelHealth.Status}. Apps are locked until the tunnel is healthy.",
                    steps,
                    warnings);
            }

            return new EdgeGatewayRelayProvisioningResult(
                true,
                false,
                relay,
                $"Cloudflare tunnel is healthy and {relayHostname} is ready for apps.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloudflareApiException exception)
        {
            return Failure($"Cloudflare API error: {exception.Message}", steps, warnings);
        }
        catch (Exception exception)
        {
            return Failure($"Relay setup failed: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayRelayRemovalResult> RemoveRelayAsync(
        string domainName,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var normalizedDomain = NormalizeDomainName(domainName);
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            string? apiToken = null;
            var accountId = string.Empty;

            if (relay is null)
            {
                return new EdgeGatewayRelayRemovalResult(
                    true,
                    normalizedDomain,
                    $"No saved relay exists for {normalizedDomain}.",
                    steps,
                    warnings);
            }

            var applicationsForDomain = configuration.Applications
                .Where(route => IsApplicationForDomain(route, normalizedDomain))
                .ToArray();
            var isProvisioned = IsRelayProvisioned(relay);
            if (isProvisioned)
            {
                apiToken = await tokenStore.GetTokenAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(apiToken))
                {
                    return new EdgeGatewayRelayRemovalResult(
                        false,
                        normalizedDomain,
                        $"Cannot delete the Cloudflare relay for {normalizedDomain} because the Cloudflare API token is not configured.",
                        steps,
                        warnings);
                }

                var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(zonesResult.Error))
                {
                    return new EdgeGatewayRelayRemovalResult(
                        false,
                        normalizedDomain,
                        zonesResult.Error,
                        steps,
                        warnings);
                }

                var zone = zonesResult.Zones.FirstOrDefault(item =>
                    item.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
                if (zone is null)
                {
                    return new EdgeGatewayRelayRemovalResult(
                        false,
                        normalizedDomain,
                        $"The saved Cloudflare token cannot manage {normalizedDomain}.",
                        steps,
                        warnings);
                }

                var records = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
                var appHostnamesToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var application in applicationsForDomain)
                {
                    try
                    {
                        var publicHostname = NormalizePublicHostname(application.PublicHostname);
                        if (string.IsNullOrWhiteSpace(publicHostname))
                        {
                            continue;
                        }

                        var hostLabel = GetHostLabelFromPublicHostname(publicHostname, normalizedDomain);
                        var relayRouteHostname = $"{hostLabel}.{relay.RelayHostname}";
                        var routeRecord = records.FirstOrDefault(record => IsDnsRecordForHostname(record, publicHostname));
                        if (routeRecord is null)
                        {
                            steps.Add($"No app DNS record was found for {publicHostname}.");
                        }
                        else if (IsSameDnsTarget(routeRecord, relayRouteHostname))
                        {
                            await dnsService.DeleteRecordAsync(apiToken, zone.Id, routeRecord.Id, cancellationToken);
                            steps.Add($"Deleted app DNS record {routeRecord.Name} -> {routeRecord.Content}.");
                        }
                        else
                        {
                            warnings.Add($"Left DNS record {routeRecord.Name} alone because it points at {routeRecord.Content}, not {relayRouteHostname}.");
                        }

                        appHostnamesToRemove.Add(publicHostname);
                    }
                    catch (Exception exception)
                    {
                        warnings.Add($"Could not clean Cloudflare DNS for {application.PublicHostname}: {exception.Message}");
                    }
                }

                var wildcardRecord = records.FirstOrDefault(record => IsWildcardRecordForDomain(record, relay.RelayHostname));
                if (wildcardRecord is null)
                {
                    steps.Add($"No wildcard DNS record was found for {relay.WildcardHostname}.");
                }
                else
                {
                    await dnsService.DeleteRecordAsync(apiToken, zone.Id, wildcardRecord.Id, cancellationToken);
                    steps.Add($"Deleted wildcard DNS record {wildcardRecord.Name} -> {wildcardRecord.Content}.");
                }

                accountId = string.IsNullOrWhiteSpace(zone.AccountId)
                    ? configuration.CloudflareTunnel.AccountId
                    : zone.AccountId;
                if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(relay.TunnelId))
                {
                    warnings.Add("Tunnel ingress could not be removed because the tunnel account or id was not saved.");
                }
                else
                {
                    var tunnelConfiguration = await tunnelService.GetConfigurationAsync(apiToken, accountId, relay.TunnelId, cancellationToken);
                    var tunnelHostnamesToRemove = appHostnamesToRemove
                        .Append(relay.WildcardHostname)
                        .Where(hostname => !string.IsNullOrWhiteSpace(hostname))
                        .Select(hostname => hostname.Trim().TrimEnd('.'))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var hadMatchingRoute = tunnelConfiguration.Routes.Any(route =>
                        !string.IsNullOrWhiteSpace(route.Hostname) &&
                        tunnelHostnamesToRemove.Contains(route.Hostname.Trim().TrimEnd('.')));
                    var updatedRoutes = RemoveHostnameTunnelRoutes(tunnelConfiguration.Routes, tunnelHostnamesToRemove);
                    if (!hadMatchingRoute)
                    {
                        steps.Add($"No tunnel ingress matched {relay.WildcardHostname} or its app hostnames.");
                    }
                    else
                    {
                        await tunnelService.UpdateConfigurationAsync(
                            apiToken,
                            accountId,
                            relay.TunnelId,
                            new CloudflareTunnelConfiguration(updatedRoutes),
                            cancellationToken);
                        steps.Add($"Removed tunnel ingress for {relay.WildcardHostname} and {appHostnamesToRemove.Count} app route(s).");
                    }
                }
            }
            else
            {
                steps.Add($"Removed local unprovisioned relay row for {normalizedDomain}.");
            }

            var remainingRelayZones = configuration.RelayZones
                .Where(existing => !existing.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                .OrderBy(existing => existing.DomainName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var shouldDeleteCloudflareTunnel = isProvisioned &&
                                                !string.IsNullOrWhiteSpace(apiToken) &&
                                                !string.IsNullOrWhiteSpace(accountId) &&
                                                !string.IsNullOrWhiteSpace(relay.TunnelId) &&
                                                !remainingRelayZones.Any(existing =>
                                                    existing.TunnelId.Equals(relay.TunnelId, StringComparison.OrdinalIgnoreCase));

            var updatedConfiguration = configuration with
            {
                RelayZones = remainingRelayZones,
                Applications = configuration.Applications
                    .Where(route => !IsApplicationForDomain(route, normalizedDomain))
                    .OrderBy(route => route.PublicHostname, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                CloudflareTunnel = remainingRelayZones.Any(IsRelayProvisioned)
                    ? configuration.CloudflareTunnel
                    : new CloudflareTunnelState(string.Empty, string.Empty, string.Empty, false, null, string.Empty)
            };

            if (applicationsForDomain.Length > 0)
            {
                steps.Add($"Removed {applicationsForDomain.Length} local app route(s) for {normalizedDomain}.");
            }

            if (!remainingRelayZones.Any(IsRelayProvisioned))
            {
                await tokenStore.ClearTunnelTokenAsync(cancellationToken);
                steps.Add("Cleared the saved cloudflared connector token because no provisioned relay zones remain.");

                var cloudflaredRestart = await TryRestartCloudflaredAsync(
                    "Restarted cloudflared after clearing the tunnel token.",
                    "cloudflared token is cleared; restart the add-on if Zero Trust still shows the connector active.",
                    cancellationToken);
                if (cloudflaredRestart.Success)
                {
                    steps.Add(cloudflaredRestart.Message);
                }
                else if (!string.IsNullOrWhiteSpace(cloudflaredRestart.Message))
                {
                    warnings.Add(cloudflaredRestart.Message);
                }
            }

            if (shouldDeleteCloudflareTunnel)
            {
                try
                {
                    await tunnelService.DeleteTunnelConnectionsAsync(apiToken!, accountId, relay.TunnelId, cancellationToken);
                    steps.Add($"Disconnected Cloudflare Tunnel connector(s) for {relay.TunnelId}.");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (CloudflareApiException exception) when (exception.StatusCode == 404)
                {
                    steps.Add($"Cloudflare Tunnel {relay.TunnelId} connections were already gone.");
                }
                catch (CloudflareApiException exception)
                {
                    warnings.Add($"Could not disconnect Cloudflare Tunnel connectors before deletion: {exception.Message}");
                }

                try
                {
                    await tunnelService.DeleteTunnelAsync(apiToken!, accountId, relay.TunnelId, cancellationToken);
                    steps.Add(string.IsNullOrWhiteSpace(relay.TunnelName)
                        ? $"Deleted Cloudflare Tunnel {relay.TunnelId}."
                        : $"Deleted Cloudflare Tunnel {relay.TunnelName} ({relay.TunnelId}).");
                }
                catch (CloudflareApiException exception) when (exception.StatusCode == 404)
                {
                    steps.Add($"Cloudflare Tunnel {relay.TunnelId} was already deleted.");
                }
                catch (CloudflareApiException exception)
                {
                    warnings.Add($"Cloudflare kept tunnel {relay.TunnelId}: {exception.Message}");
                }
            }

            var caddyResult = await ApplyCaddyConfigurationAsync(updatedConfiguration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);

            return new EdgeGatewayRelayRemovalResult(
                true,
                normalizedDomain,
                $"Deleted relay setup for {normalizedDomain}.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloudflareApiException exception)
        {
            return new EdgeGatewayRelayRemovalResult(false, domainName, $"Cloudflare API error: {exception.Message}", steps, warnings);
        }
        catch (Exception exception)
        {
            return new EdgeGatewayRelayRemovalResult(false, domainName, $"Relay delete failed: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayRelayValidationResult> ValidateRelayAsync(
        string domainName,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var normalizedDomain = NormalizeDomainName(domainName);
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (relay is null)
            {
                return new EdgeGatewayRelayValidationResult(
                    false,
                    null,
                    $"No saved relay exists for {normalizedDomain}.",
                    "missing",
                    steps,
                    warnings);
            }

            if (!IsRelayProvisioned(relay))
            {
                var unprovisionedRelay = UpdateRelayTunnelStatus(
                    relay,
                    new TunnelHealthCheck(false, null, "not-provisioned", "Setup Relay has not completed for this domain."));
                await SaveRelayStatusAsync(configuration, unprovisionedRelay, cancellationToken);
                return new EdgeGatewayRelayValidationResult(
                    false,
                    unprovisionedRelay,
                    "Setup Relay has not completed for this domain.",
                    unprovisionedRelay.TunnelStatus,
                    steps,
                    warnings);
            }

            var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                var uncheckedRelay = UpdateRelayTunnelStatus(
                    relay,
                    new TunnelHealthCheck(false, null, "unknown", "Cloudflare API token is not configured."));
                await SaveRelayStatusAsync(configuration, uncheckedRelay, cancellationToken);
                return new EdgeGatewayRelayValidationResult(
                    false,
                    uncheckedRelay,
                    uncheckedRelay.LastValidationError,
                    uncheckedRelay.TunnelStatus,
                    steps,
                    warnings);
            }

            var accountId = configuration.CloudflareTunnel.AccountId;
            if (string.IsNullOrWhiteSpace(accountId))
            {
                var uncheckedRelay = UpdateRelayTunnelStatus(
                    relay,
                    new TunnelHealthCheck(false, null, "unknown", "Cloudflare account id was not saved for this tunnel."));
                await SaveRelayStatusAsync(configuration, uncheckedRelay, cancellationToken);
                return new EdgeGatewayRelayValidationResult(
                    false,
                    uncheckedRelay,
                    uncheckedRelay.LastValidationError,
                    uncheckedRelay.TunnelStatus,
                    steps,
                    warnings);
            }

            var health = await CheckTunnelHealthAsync(apiToken, accountId, relay.TunnelId, cancellationToken);
            steps.Add($"Cloudflare tunnel status: {health.Status}.");
            if (!health.Success)
            {
                await EnsureCloudflaredConnectorTokenAsync(
                    apiToken,
                    accountId,
                    relay.TunnelId,
                    steps,
                    warnings,
                    cancellationToken);

                var restart = await TryRestartCloudflaredAsync(
                    "Restarted cloudflared while validating the relay.",
                    "cloudflared connector token is saved, but no supervised cloudflared service was found.",
                    cancellationToken);
                if (restart.Success)
                {
                    steps.Add(restart.Message);
                    health = await WaitForTunnelHealthyAsync(apiToken, accountId, relay.TunnelId, steps, warnings, cancellationToken);
                }
                else if (!string.IsNullOrWhiteSpace(restart.Message))
                {
                    warnings.Add(restart.Message);
                }
            }

            var updatedRelay = UpdateRelayTunnelStatus(relay, health);
            await SaveRelayStatusAsync(configuration, updatedRelay, cancellationToken);

            return new EdgeGatewayRelayValidationResult(
                health.Success,
                updatedRelay,
                health.Summary,
                health.Status,
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayRelayValidationResult(false, null, $"Relay validation failed: {exception.Message}", "unknown", steps, warnings);
        }
    }

    public async Task<EdgeGatewayRelayValidationResult> RepairRelayAsync(
        string domainName,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var normalizedDomain = NormalizeDomainName(domainName);
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (relay is null)
            {
                return new EdgeGatewayRelayValidationResult(
                    false,
                    null,
                    $"No saved relay exists for {normalizedDomain}.",
                    "missing",
                    steps,
                    warnings);
            }

            if (!IsRelayProvisioned(relay))
            {
                return new EdgeGatewayRelayValidationResult(
                    false,
                    relay,
                    "Setup Relay has not completed for this domain.",
                    "not-provisioned",
                    steps,
                    warnings);
            }

            var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                return await SaveAndReturnRepairFailureAsync(
                    configuration,
                    relay,
                    "unknown",
                    "Cloudflare API token is not configured.",
                    steps,
                    warnings,
                    cancellationToken);
            }

            var accountId = configuration.CloudflareTunnel.AccountId;
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return await SaveAndReturnRepairFailureAsync(
                    configuration,
                    relay,
                    "unknown",
                    "Cloudflare account id was not saved for this tunnel.",
                    steps,
                    warnings,
                    cancellationToken);
            }

            steps.Add($"Repairing relay {normalizedDomain} without deleting Cloudflare or Caddy configuration.");

            try
            {
                var connectorToken = await tunnelService.GetTunnelTokenAsync(
                    apiToken,
                    accountId,
                    relay.TunnelId,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(connectorToken))
                {
                    warnings.Add("Cloudflare did not return a connector token for this tunnel.");
                }
                else
                {
                    await tokenStore.SaveTunnelTokenAsync(connectorToken, cancellationToken);
                    steps.Add("Fetched the current Cloudflare Tunnel connector token and saved it for cloudflared.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Cloudflare connector token could not be refreshed: {exception.Message}");
            }

            var caddyResult = await ApplyCaddyConfigurationAsync(configuration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            var restart = await TryRestartCloudflaredAsync(
                "Restarted cloudflared with the repaired connector token.",
                "cloudflared connector token is saved, but no supervised cloudflared service was found.",
                cancellationToken);
            if (restart.Success)
            {
                steps.Add(restart.Message);
            }
            else if (!string.IsNullOrWhiteSpace(restart.Message))
            {
                warnings.Add(restart.Message);
            }

            var health = restart.Success
                ? await WaitForTunnelHealthyAsync(apiToken, accountId, relay.TunnelId, steps, warnings, cancellationToken)
                : await CheckTunnelHealthAsync(apiToken, accountId, relay.TunnelId, cancellationToken);

            if (!health.Success)
            {
                foreach (var diagnostic in await GetCloudflaredDiagnosticsAsync(cancellationToken))
                {
                    warnings.Add(diagnostic);
                }
            }

            var updatedRelay = UpdateRelayTunnelStatus(relay, health);
            await SaveRelayStatusAsync(configuration, updatedRelay, cancellationToken);

            var success = health.Success && caddyResult.Success;
            var summary = success
                ? $"Repair complete: {normalizedDomain} tunnel is healthy and Caddy configuration is loaded."
                : BuildRepairFailureSummary(health, caddyResult);
            return new EdgeGatewayRelayValidationResult(
                success,
                updatedRelay,
                summary,
                health.Status,
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Add(exception.Message);
            return new EdgeGatewayRelayValidationResult(
                false,
                null,
                $"Relay repair failed: {exception.Message}",
                "unknown",
                steps,
                warnings);
        }
    }

    public async Task<EdgeGatewayApplicationSaveResult> AddApplicationAsync(
        string domainName,
        string name,
        string hostLabel,
        string targetScheme,
        string targetHost,
        int targetPort,
        string accessPolicy,
        string targetPathPrefix = "",
        bool isEnabled = true,
        string allowKnownIps = "",
        string allowedUsers = "",
        string allowedGroups = "",
        bool allowLanOnly = false,
        string notes = "",
        bool? usePublicHostHeader = null,
        bool? stripForwardedFor = null,
        bool? skipUpstreamTlsVerification = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var normalizedDomain = NormalizeDomainName(domainName);
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (relay is null || !IsRelayProvisioned(relay))
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    null,
                    $"Setup Relay must complete for {normalizedDomain} before adding apps.",
                    steps,
                    warnings);
            }

            var relayValidation = await ValidateRelayAsync(normalizedDomain, cancellationToken);
            steps.AddRange(relayValidation.Steps);
            warnings.AddRange(relayValidation.Warnings);
            if (!relayValidation.Success)
            {
                warnings.Add($"Relay health check did not pass before saving the app route: {relayValidation.Summary}");
            }

            configuration = await configurationStore.LoadAsync(cancellationToken);
            var normalizedName = string.IsNullOrWhiteSpace(name) ? "Home Assistant" : name.Trim();
            var normalizedHostLabel = NormalizeHostLabelForDomain(hostLabel, normalizedDomain);
            var normalizedPathPrefix = NormalizeRoutePathPrefix(targetPathPrefix);
            var targetOrigin = NormalizeTargetOrigin(targetScheme, targetHost, targetPort);
            var publicHostname = $"{normalizedHostLabel}.{normalizedDomain}";
            if (configuration.Applications.Any(route =>
                    route.PublicHostname.Equals(publicHostname, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeRoutePathPrefix(route.TargetPathPrefix).Equals(normalizedPathPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    null,
                    $"{BuildRouteUrlDisplay(publicHostname, normalizedPathPrefix)} is already saved.",
                    steps,
                    warnings);
            }

            var application = new PublishedApplicationDefinition(
                Guid.NewGuid(),
                normalizedName,
                publicHostname,
                targetOrigin,
                string.IsNullOrWhiteSpace(accessPolicy) ? "MFA/Passkey" : accessPolicy.Trim(),
                isEnabled,
                normalizedPathPrefix,
                NormalizeRouteTextBlock(allowKnownIps),
                NormalizeRouteTextBlock(allowedUsers),
                NormalizeRouteTextBlock(allowedGroups),
                allowLanOnly,
                NormalizeRouteTextBlock(notes),
                usePublicHostHeader,
                stripForwardedFor,
                skipUpstreamTlsVerification);
            AddHomeAssistantUpstreamWarnings(application, warnings);

            var updatedConfiguration = configuration with
            {
                Applications = configuration.Applications
                    .Append(application)
                    .OrderBy(route => route.PublicHostname, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            var caddyResult = await ApplyCaddyConfigurationAsync(updatedConfiguration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);
            steps.Add($"Saved app route {BuildRouteUrlDisplay(publicHostname, normalizedPathPrefix)} -> {targetOrigin}.");

            var publishResult = await PublishApplicationAsync(application.Id, cancellationToken: cancellationToken);
            steps.AddRange(publishResult.Steps);
            warnings.AddRange(publishResult.Warnings);
            if (!publishResult.Success)
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    publishResult.Application ?? application,
                    $"{normalizedName} was saved, but the public route was not made available: {publishResult.Summary}",
                    steps,
                    warnings);
            }

            return new EdgeGatewayApplicationSaveResult(
                true,
                publishResult.Application ?? application,
                $"{normalizedName} is available at {BuildRouteUrlDisplay(publicHostname, normalizedPathPrefix)}.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Could not add app: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayApplicationSaveResult> UpdateApplicationAsync(
        Guid applicationId,
        string name,
        string hostLabel,
        string targetScheme,
        string targetHost,
        int targetPort,
        string accessPolicy,
        string targetPathPrefix = "",
        string allowKnownIps = "",
        string allowedUsers = "",
        string allowedGroups = "",
        bool allowLanOnly = false,
        string notes = "",
        bool? usePublicHostHeader = null,
        bool? stripForwardedFor = null,
        bool? skipUpstreamTlsVerification = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var existing = configuration.Applications.FirstOrDefault(route => route.Id == applicationId);
            if (existing is null)
            {
                return new EdgeGatewayApplicationSaveResult(false, null, "The app route no longer exists.", steps, warnings);
            }

            var normalizedDomain = GetDomainNameFromPublicHostname(existing.PublicHostname);
            var normalizedHostLabel = NormalizeHostLabelForDomain(hostLabel, normalizedDomain);
            var normalizedPathPrefix = NormalizeRoutePathPrefix(targetPathPrefix);
            var publicHostname = $"{normalizedHostLabel}.{normalizedDomain}";
            if (configuration.Applications.Any(route =>
                    route.Id != applicationId &&
                    route.PublicHostname.Equals(publicHostname, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeRoutePathPrefix(route.TargetPathPrefix).Equals(normalizedPathPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                return new EdgeGatewayApplicationSaveResult(false, null, $"{BuildRouteUrlDisplay(publicHostname, normalizedPathPrefix)} is already saved.", steps, warnings);
            }

            var hostnameChanged = !existing.PublicHostname.Equals(publicHostname, StringComparison.OrdinalIgnoreCase);
            if (hostnameChanged)
            {
                warnings.Add($"The public hostname changed from {existing.PublicHostname} to {publicHostname}. Cloudflare DNS and tunnel ingress will be refreshed now.");
            }

            var existingPathPrefix = NormalizeRoutePathPrefix(existing.TargetPathPrefix);
            if (!existingPathPrefix.Equals(normalizedPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"The URL path prefix changed from {BuildPathDisplay(existingPathPrefix)} to {BuildPathDisplay(normalizedPathPrefix)}. Caddy will reload with the new route match.");
            }

            var updated = existing with
            {
                Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim(),
                PublicHostname = publicHostname,
                UpstreamUrl = NormalizeTargetOrigin(targetScheme, targetHost, targetPort),
                AccessPolicy = string.IsNullOrWhiteSpace(accessPolicy) ? "MFA/Passkey" : accessPolicy.Trim(),
                TargetPathPrefix = normalizedPathPrefix,
                AllowKnownIps = NormalizeRouteTextBlock(allowKnownIps),
                AllowedUsers = NormalizeRouteTextBlock(allowedUsers),
                AllowedGroups = NormalizeRouteTextBlock(allowedGroups),
                AllowLanOnly = allowLanOnly,
                Notes = NormalizeRouteTextBlock(notes),
                UsePublicHostHeader = usePublicHostHeader,
                StripForwardedFor = stripForwardedFor,
                SkipUpstreamTlsVerification = skipUpstreamTlsVerification
            };
            AddHomeAssistantUpstreamWarnings(updated, warnings);

            var updatedConfiguration = configuration with
            {
                Applications = configuration.Applications
                    .Select(route => route.Id == applicationId ? updated : route)
                    .OrderBy(route => route.PublicHostname, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            var caddyResult = await ApplyCaddyConfigurationAsync(updatedConfiguration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);
            steps.Add($"Saved app route {BuildRouteUrlDisplay(updated.PublicHostname, updated.TargetPathPrefix)} -> {updated.UpstreamUrl}.");

            var publishResult = await PublishApplicationAsync(applicationId, cancellationToken: cancellationToken);
            steps.AddRange(publishResult.Steps);
            warnings.AddRange(publishResult.Warnings);
            if (!publishResult.Success)
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    publishResult.Application ?? updated,
                    $"{updated.Name} was saved locally, but Cloudflare DNS/tunnel ingress was not refreshed: {publishResult.Summary}",
                    steps,
                    warnings);
            }

            if (hostnameChanged)
            {
                await RemoveStaleApplicationCloudflareResourcesAsync(updatedConfiguration, existing, steps, warnings, cancellationToken);
            }

            return new EdgeGatewayApplicationSaveResult(
                true,
                publishResult.Application ?? updated,
                $"{updated.Name} was updated and Cloudflare DNS/tunnel ingress was refreshed.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Could not update app: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayApplicationSaveResult> PublishApplicationAsync(
        Guid applicationId,
        bool replaceExistingDnsRecord = false,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var application = configuration.Applications.FirstOrDefault(route => route.Id == applicationId);
            if (application is null)
            {
                return new EdgeGatewayApplicationSaveResult(false, null, "The app route no longer exists.", steps, warnings);
            }

            var normalizedDomain = GetDomainNameFromPublicHostname(application.PublicHostname);
            var normalizedHostLabel = GetHostLabelFromPublicHostname(application.PublicHostname, normalizedDomain);
            var publicHostname = $"{normalizedHostLabel}.{normalizedDomain}";
            var relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (relay is null || !IsRelayProvisioned(relay))
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    application,
                    $"Setup Relay must complete for {normalizedDomain} before publishing apps.",
                    steps,
                    warnings);
            }

            var relayValidation = await ValidateRelayAsync(normalizedDomain, cancellationToken);
            steps.AddRange(relayValidation.Steps);
            warnings.AddRange(relayValidation.Warnings);
            if (!relayValidation.Success)
            {
                warnings.Add($"Relay health check did not pass before publishing the app route: {relayValidation.Summary}");
            }

            configuration = await configurationStore.LoadAsync(cancellationToken);
            application = configuration.Applications.FirstOrDefault(route => route.Id == applicationId);
            if (application is null)
            {
                return new EdgeGatewayApplicationSaveResult(false, null, "The app route no longer exists.", steps, warnings);
            }

            relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (relay is null || !IsRelayProvisioned(relay))
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    application,
                    $"Setup Relay must complete for {normalizedDomain} before publishing apps.",
                    steps,
                    warnings);
            }

            var relayHostname = $"{normalizedHostLabel}.{relay.RelayHostname}";
            var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                return new EdgeGatewayApplicationSaveResult(false, application, "Cloudflare API token is not configured.", steps, warnings);
            }

            var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(zonesResult.Error))
            {
                return new EdgeGatewayApplicationSaveResult(false, application, zonesResult.Error, steps, warnings);
            }

            var zone = zonesResult.Zones.FirstOrDefault(item =>
                item.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            if (zone is null)
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    application,
                    $"The saved Cloudflare token cannot manage {normalizedDomain}.",
                    steps,
                    warnings);
            }

            if (string.IsNullOrWhiteSpace(zone.AccountId))
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    application,
                    $"Cloudflare did not return an account id for {normalizedDomain}. Check the token account scope.",
                    steps,
                    warnings);
            }

            steps.Add($"Cloudflare zone found: {normalizedDomain} in {zone.AccountName}.");

            var existingRecords = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
            var existingRouteRecord = existingRecords.FirstOrDefault(record =>
                IsDnsRecordForHostname(record, publicHostname));
            if (existingRouteRecord is null)
            {
                await dnsService.CreateRecordAsync(
                    apiToken,
                    zone.Id,
                    new CloudflareDnsRecord(
                        string.Empty,
                        zone.Id,
                        publicHostname,
                        "CNAME",
                        relayHostname,
                        true,
                        1,
                        options.Value.ManagedRecordComment,
                        null),
                    cancellationToken);
                steps.Add($"Created proxied route DNS record {publicHostname} -> {relayHostname}.");
            }
            else if (IsSameDnsTarget(existingRouteRecord, relayHostname))
            {
                steps.Add($"Route DNS record already points at {relayHostname}.");
            }
            else
            {
                if (!replaceExistingDnsRecord)
                {
                    warnings.Add($"Existing route DNS record: {existingRouteRecord.Type} {existingRouteRecord.Name} -> {existingRouteRecord.Content}.");
                    return new EdgeGatewayApplicationSaveResult(
                        false,
                        application,
                        $"DNS already exists for {publicHostname} and points at {existingRouteRecord.Content}. Confirm replacement to point it at {relayHostname}.",
                        steps,
                        warnings);
                }

                await dnsService.DeleteRecordAsync(apiToken, zone.Id, existingRouteRecord.Id, cancellationToken);
                await dnsService.CreateRecordAsync(
                    apiToken,
                    zone.Id,
                    new CloudflareDnsRecord(
                        string.Empty,
                        zone.Id,
                        publicHostname,
                        "CNAME",
                        relayHostname,
                        true,
                        1,
                        options.Value.ManagedRecordComment,
                        null),
                    cancellationToken);
                steps.Add($"Replaced route DNS record with {publicHostname} -> {relayHostname}.");
            }

            var caddyServiceUrl = ResolveCaddyServiceUrl();
            var tunnelConfiguration = await tunnelService.GetConfigurationAsync(apiToken, zone.AccountId, relay.TunnelId, cancellationToken);
            await tunnelService.UpdateConfigurationAsync(
                apiToken,
                zone.AccountId,
                relay.TunnelId,
                new CloudflareTunnelConfiguration(MergeHostnameTunnelRoute(
                    tunnelConfiguration.Routes,
                    publicHostname,
                    caddyServiceUrl,
                    ShouldSkipUpstreamTlsVerification(application))),
                cancellationToken);
            steps.Add($"Configured tunnel ingress {publicHostname} -> {caddyServiceUrl}.");
            if (ShouldSkipUpstreamTlsVerification(application))
            {
                steps.Add($"Enabled Cloudflare tunnel originRequest noTLSVerify for HTTPS upstream {application.UpstreamUrl}.");
            }

            var caddyResult = await ApplyCaddyConfigurationAsync(configuration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            if (!application.IsEnabled)
            {
                return new EdgeGatewayApplicationSaveResult(
                    true,
                    application,
                    $"{application.Name} DNS and tunnel ingress are ready. Enable the app to load the Caddy route.",
                    steps,
                    warnings);
            }

            var caddyRouteCheck = await TestCaddyApplicationRouteAsync(application, cancellationToken);
            steps.Add(caddyRouteCheck.Summary);
            if (!caddyRouteCheck.Success)
            {
                return new EdgeGatewayApplicationSaveResult(
                    false,
                    application,
                    $"{application.Name} DNS and tunnel ingress were updated, but Caddy did not forward {publicHostname}: {caddyRouteCheck.Summary}",
                    steps,
                    warnings);
            }

            return new EdgeGatewayApplicationSaveResult(
                true,
                application,
                $"{application.Name} now points through {relayHostname} to {application.UpstreamUrl}.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloudflareApiException exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Cloudflare API error: {exception.Message}", steps, warnings);
        }
        catch (Exception exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Could not publish app: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayApplicationSaveResult> SetApplicationEnabledAsync(
        Guid applicationId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var application = configuration.Applications.FirstOrDefault(route => route.Id == applicationId);
            if (application is null)
            {
                return new EdgeGatewayApplicationSaveResult(false, null, "The app route no longer exists.", steps, warnings);
            }

            var updated = application with { IsEnabled = enabled };
            var updatedConfiguration = configuration with
            {
                Applications = configuration.Applications
                    .Select(route => route.Id == applicationId ? updated : route)
                    .ToArray()
            };

            var caddyResult = await ApplyCaddyConfigurationAsync(updatedConfiguration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);
            return new EdgeGatewayApplicationSaveResult(
                true,
                updated,
                enabled ? $"{updated.Name} is enabled." : $"{updated.Name} is disabled.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Could not update app state: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayApplicationSaveResult> RemoveApplicationAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var application = configuration.Applications.FirstOrDefault(route => route.Id == applicationId);
            if (application is null)
            {
                return new EdgeGatewayApplicationSaveResult(true, null, "The app route was already removed.", steps, warnings);
            }

            var normalizedDomain = GetDomainNameFromPublicHostname(application.PublicHostname);
            var normalizedHostLabel = GetHostLabelFromPublicHostname(application.PublicHostname, normalizedDomain);
            var publicHostname = $"{normalizedHostLabel}.{normalizedDomain}";
            var relay = configuration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            var remainingRoutesForHostname = configuration.Applications.Any(route =>
                route.Id != applicationId &&
                route.PublicHostname.Equals(publicHostname, StringComparison.OrdinalIgnoreCase));

            var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
            if (!remainingRoutesForHostname && !string.IsNullOrWhiteSpace(apiToken) && relay is not null && IsRelayProvisioned(relay))
            {
                var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
                var zone = string.IsNullOrWhiteSpace(zonesResult.Error)
                    ? zonesResult.Zones.FirstOrDefault(item => item.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (zone is null)
                {
                    warnings.Add(string.IsNullOrWhiteSpace(zonesResult.Error)
                        ? $"The saved Cloudflare token cannot manage {normalizedDomain}; local route will still be removed."
                        : zonesResult.Error);
                }
                else
                {
                    var relayHostname = $"{normalizedHostLabel}.{relay.RelayHostname}";
                    var records = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
                    var routeRecord = records.FirstOrDefault(record => IsDnsRecordForHostname(record, publicHostname));
                    if (routeRecord is not null && IsSameDnsTarget(routeRecord, relayHostname))
                    {
                        await dnsService.DeleteRecordAsync(apiToken, zone.Id, routeRecord.Id, cancellationToken);
                        steps.Add($"Deleted route DNS record {routeRecord.Name} -> {routeRecord.Content}.");
                    }

                    var accountId = string.IsNullOrWhiteSpace(zone.AccountId)
                        ? configuration.CloudflareTunnel.AccountId
                        : zone.AccountId;
                    if (!string.IsNullOrWhiteSpace(accountId) && !string.IsNullOrWhiteSpace(relay.TunnelId))
                    {
                        var tunnelConfiguration = await tunnelService.GetConfigurationAsync(apiToken, accountId, relay.TunnelId, cancellationToken);
                        await tunnelService.UpdateConfigurationAsync(
                            apiToken,
                            accountId,
                            relay.TunnelId,
                            new CloudflareTunnelConfiguration(RemoveHostnameTunnelRoute(tunnelConfiguration.Routes, publicHostname)),
                            cancellationToken);
                        steps.Add($"Removed tunnel ingress for {publicHostname}.");
                    }
                }
            }
            else if (remainingRoutesForHostname)
            {
                steps.Add($"Kept Cloudflare DNS and tunnel ingress for {publicHostname} because another path route still uses that hostname.");
            }

            var updatedConfiguration = configuration with
            {
                Applications = configuration.Applications
                    .Where(route => route.Id != applicationId)
                    .ToArray()
            };

            var caddyResult = await ApplyCaddyConfigurationAsync(updatedConfiguration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);
            return new EdgeGatewayApplicationSaveResult(true, application, $"Deleted {application.Name}.", steps, warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloudflareApiException exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Cloudflare API error: {exception.Message}", steps, warnings);
        }
        catch (Exception exception)
        {
            return new EdgeGatewayApplicationSaveResult(false, null, $"Could not delete app: {exception.Message}", steps, warnings);
        }
    }

    public async Task<EdgeGatewayApplicationTestResult> TestApplicationAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var application = configuration.Applications.FirstOrDefault(route => route.Id == applicationId);
            if (application is null)
            {
                return new EdgeGatewayApplicationTestResult(false, applicationId, "The app route no longer exists.", checks, warnings);
            }

            using var client = CreateRouteHttpClient(TimeSpan.FromSeconds(6));
            var caddyHealthUrl = $"{ResolveCaddyServiceUrl().TrimEnd('/')}/health";
            var caddyResponse = await client.GetAsync(caddyHealthUrl, cancellationToken);
            checks.Add($"Caddy health {caddyHealthUrl}: {(int)caddyResponse.StatusCode} {caddyResponse.ReasonPhrase}.");

            var targetResponse = await client.GetAsync(application.UpstreamUrl, cancellationToken);
            checks.Add($"Internal target {application.UpstreamUrl}: {(int)targetResponse.StatusCode} {targetResponse.ReasonPhrase}.");

            var caddyRouteCheck = await TestCaddyApplicationRouteAsync(application, cancellationToken);
            checks.Add(caddyRouteCheck.Summary);

            var success = caddyResponse.IsSuccessStatusCode && (int)targetResponse.StatusCode < 500 && caddyRouteCheck.Success;
            return new EdgeGatewayApplicationTestResult(
                success,
                applicationId,
                success ? $"{application.Name} Caddy route and target responded." : $"{application.Name} route test found a problem.",
                checks,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            checks.Add(exception.Message);
            return new EdgeGatewayApplicationTestResult(false, applicationId, $"Route test failed: {exception.Message}", checks, warnings);
        }
    }

    public async Task<EdgeGatewayCaddyConfigurationResult> RefreshCaddyConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var result = await ApplyCaddyConfigurationAsync(configuration, cancellationToken);
            return new EdgeGatewayCaddyConfigurationResult(
                result.Success,
                result.Message,
                result.Success ? [] : [result.Message]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayCaddyConfigurationResult(
                false,
                $"Could not refresh Caddy configuration: {exception.Message}",
                [$"Could not refresh Caddy configuration: {exception.Message}"]);
        }
    }

    public async Task<EdgeGatewayCaddyConfigurationResult> RefreshPublishedConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var caddyResult = await ApplyCaddyConfigurationAsync(configuration, cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Message);
            }
            else
            {
                warnings.Add(caddyResult.Message);
            }

            await ReconcileCloudflareTunnelIngressAsync(configuration, steps, warnings, cancellationToken);
            await ValidateConfiguredRelaysAsync(configuration, steps, warnings, cancellationToken);

            var summary = steps.Count == 0
                ? "No published configuration needed refreshing."
                : string.Join(" ", steps);
            return new EdgeGatewayCaddyConfigurationResult(caddyResult.Success, summary, warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EdgeGatewayCaddyConfigurationResult(
                false,
                $"Could not refresh published configuration: {exception.Message}",
                [$"Could not refresh published configuration: {exception.Message}"]);
        }
    }

    private async Task ValidateConfiguredRelaysAsync(
        EdgeGatewayConfiguration configuration,
        ICollection<string> steps,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var relay in configuration.RelayZones.Where(IsRelayProvisioned))
        {
            var validation = await ValidateRelayAsync(relay.DomainName, cancellationToken);
            steps.Add($"Relay {relay.DomainName} validation: {validation.Summary}");
            foreach (var step in validation.Steps)
            {
                steps.Add(step);
            }

            foreach (var warning in validation.Warnings)
            {
                warnings.Add(warning);
            }
        }
    }

    private async Task ReconcileCloudflareTunnelIngressAsync(
        EdgeGatewayConfiguration configuration,
        ICollection<string> steps,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var relays = configuration.RelayZones
            .Where(IsRelayProvisioned)
            .ToArray();
        if (relays.Length == 0)
        {
            return;
        }

        var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            warnings.Add("Cloudflare API token is not configured; skipped startup tunnel ingress reconciliation.");
            return;
        }

        var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(zonesResult.Error))
        {
            warnings.Add($"Cloudflare zones could not be loaded; skipped startup tunnel ingress reconciliation: {zonesResult.Error}");
            return;
        }

        var caddyServiceUrl = ResolveCaddyServiceUrl();
        foreach (var relay in relays)
        {
            var zone = zonesResult.Zones.FirstOrDefault(item =>
                item.Name.Equals(relay.DomainName, StringComparison.OrdinalIgnoreCase));
            var accountId = !string.IsNullOrWhiteSpace(zone?.AccountId)
                ? zone.AccountId
                : configuration.CloudflareTunnel.AccountId;
            if (string.IsNullOrWhiteSpace(accountId))
            {
                warnings.Add($"Cloudflare account id is not known for {relay.DomainName}; skipped tunnel ingress reconciliation.");
                continue;
            }

            var tunnelId = !string.IsNullOrWhiteSpace(relay.TunnelId)
                ? relay.TunnelId
                : configuration.CloudflareTunnel.TunnelId;
            if (string.IsNullOrWhiteSpace(tunnelId))
            {
                warnings.Add($"Cloudflare tunnel id is not known for {relay.DomainName}; skipped tunnel ingress reconciliation.");
                continue;
            }

            var existing = await tunnelService.GetConfigurationAsync(apiToken, accountId, tunnelId, cancellationToken);
            var applications = configuration.Applications
                .Where(application => IsApplicationForDomain(application, relay.DomainName))
                .ToArray();
            var reconciledRoutes = RebuildManagedTunnelIngressRoutes(existing.Routes, relay, applications, caddyServiceUrl);
            if (TunnelRoutesEqual(existing.Routes, reconciledRoutes))
            {
                steps.Add($"Cloudflare tunnel ingress already matches saved apps for {relay.DomainName}.");
                continue;
            }

            await tunnelService.UpdateConfigurationAsync(
                apiToken,
                accountId,
                tunnelId,
                new CloudflareTunnelConfiguration(reconciledRoutes),
                cancellationToken);
            steps.Add($"Reconciled Cloudflare tunnel ingress for {relay.DomainName} ({applications.Length} saved app route(s)).");
        }
    }

    private async Task<CaddyRouteCheck> TestCaddyApplicationRouteAsync(
        PublishedApplicationDefinition application,
        CancellationToken cancellationToken)
    {
        var hostname = NormalizePublicHostname(application.PublicHostname);
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return new CaddyRouteCheck(false, $"Caddy route check skipped because {application.PublicHostname} is not a valid hostname.");
        }

        var pathPrefix = NormalizeRoutePathPrefix(application.TargetPathPrefix);
        var requestPath = string.IsNullOrWhiteSpace(pathPrefix) ? "/" : pathPrefix;
        var caddyUrl = $"{ResolveCaddyServiceUrl().TrimEnd('/')}{requestPath}";
        using var client = CreateRouteHttpClient(TimeSpan.FromSeconds(8));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, caddyUrl);
            request.Headers.Host = hostname;
            request.Headers.UserAgent.ParseAdd("LinuxMadeSane-edge-caddy-route-check");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var snippet = await TryReadResponseSnippetAsync(response, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound &&
                snippet.Contains(FallbackCaddyResponse, StringComparison.OrdinalIgnoreCase))
            {
                return new CaddyRouteCheck(
                    false,
                    $"Caddy route check {hostname}{requestPath} via {ResolveCaddyServiceUrl()} returned the Edge Gateway fallback 404. The Host matcher was not loaded.");
            }

            if ((int)response.StatusCode >= 500)
            {
                var homeAssistantHint = BuildHomeAssistantRouteCheckHint(application);
                return new CaddyRouteCheck(
                    false,
                    $"Caddy route check {hostname}{requestPath} via {ResolveCaddyServiceUrl()} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Upstream {application.UpstreamUrl} was not reachable from Caddy.{homeAssistantHint}");
            }

            if (IsBlockedAccessPolicy(application.AccessPolicy) &&
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return new CaddyRouteCheck(true, $"Caddy route check {hostname}{requestPath} returned HTTP 403 as expected because the route is blocked.");
            }

            return new CaddyRouteCheck(
                true,
                $"Caddy route check {hostname}{requestPath} via {ResolveCaddyServiceUrl()} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CaddyRouteCheck(
                false,
                $"Caddy route check {hostname}{requestPath} via {ResolveCaddyServiceUrl()} failed: {exception.Message}");
        }
    }

    private static HttpClient CreateRouteHttpClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler)
        {
            Timeout = timeout
        };
    }

    private static string BuildHomeAssistantRouteCheckHint(PublishedApplicationDefinition application)
    {
        if (!IsHomeAssistantRoute(application) ||
            !Uri.TryCreate(application.UpstreamUrl, UriKind.Absolute, out var upstream) ||
            upstream.Port != 8123 ||
            !upstream.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return " If this Home Assistant instance has SSL enabled, change the target scheme to https and leave upstream TLS verification disabled.";
    }

    private static async Task<string> TryReadResponseSnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            return read <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<EdgeGatewayRelayValidationResult> SaveAndReturnRepairFailureAsync(
        EdgeGatewayConfiguration configuration,
        EdgeGatewayRelayZone relay,
        string status,
        string summary,
        IReadOnlyList<string> steps,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken)
    {
        var uncheckedRelay = UpdateRelayTunnelStatus(
            relay,
            new TunnelHealthCheck(false, null, status, summary));
        await SaveRelayStatusAsync(configuration, uncheckedRelay, cancellationToken);
        return new EdgeGatewayRelayValidationResult(
            false,
            uncheckedRelay,
            summary,
            uncheckedRelay.TunnelStatus,
            steps,
            warnings);
    }

    private static string BuildRepairFailureSummary(
        TunnelHealthCheck health,
        CaddyApplyAttempt caddyResult)
    {
        if (!caddyResult.Success && !health.Success)
        {
            return $"Repair ran, but Caddy reload failed and Cloudflare still reports the tunnel as {health.Status}: {FirstNonEmpty(caddyResult.Message, health.Summary)}";
        }

        if (!caddyResult.Success)
        {
            return $"Tunnel is healthy, but Caddy reload failed: {caddyResult.Message}";
        }

        return $"Repair ran, but Cloudflare still reports the tunnel as {health.Status}: {health.Summary}";
    }

    private async Task EnsureCloudflaredConnectorTokenAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        ICollection<string> steps,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var existingToken = await tokenStore.GetTunnelTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingToken))
        {
            return;
        }

        try
        {
            var connectorToken = await tunnelService.GetTunnelTokenAsync(
                apiToken,
                accountId,
                tunnelId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(connectorToken))
            {
                warnings.Add("Cloudflare did not return a connector token for this tunnel.");
                return;
            }

            await tokenStore.SaveTunnelTokenAsync(connectorToken, cancellationToken);
            steps.Add("Recovered and saved the cloudflared connector token for this tunnel.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"Cloudflare connector token could not be recovered: {exception.Message}");
        }
    }

    private async Task<TunnelHealthCheck> WaitForTunnelHealthyAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        ICollection<string> steps,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        TunnelHealthCheck? lastCheck = null;
        for (var attempt = 1; attempt <= TunnelValidationAttempts; attempt++)
        {
            lastCheck = await CheckTunnelHealthAsync(apiToken, accountId, tunnelId, cancellationToken);
            steps.Add($"Cloudflare tunnel status check {attempt}/{TunnelValidationAttempts}: {lastCheck.Status}.");
            if (lastCheck.Success)
            {
                return lastCheck;
            }

            if (attempt < TunnelValidationAttempts)
            {
                await Task.Delay(TunnelValidationDelay, cancellationToken);
            }
        }

        var failedCheck = lastCheck ?? new TunnelHealthCheck(false, null, "unknown", "Cloudflare tunnel status could not be checked.");
        warnings.Add("Cloudflare did not report the tunnel as healthy before the setup timeout.");
        return failedCheck;
    }

    private async Task<TunnelHealthCheck> CheckTunnelHealthAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        var tunnel = await tunnelService.GetTunnelAsync(apiToken, accountId, tunnelId, cancellationToken);
        if (tunnel is null)
        {
            return new TunnelHealthCheck(false, null, "missing", $"Cloudflare Tunnel {tunnelId} was not found.");
        }

        var status = NormalizeTunnelStatus(tunnel.Status);
        var success = IsTunnelHealthy(status);
        return new TunnelHealthCheck(
            success,
            tunnel,
            status,
            success
                ? $"Cloudflare reports tunnel {tunnel.Name} is healthy."
                : $"Cloudflare reports tunnel {tunnel.Name} as {status}; external access waits for cloudflared to reconnect.");
    }

    private static EdgeGatewayRelayZone UpdateRelayTunnelStatus(
        EdgeGatewayRelayZone relay,
        TunnelHealthCheck health) =>
        relay with
        {
            TunnelName = string.IsNullOrWhiteSpace(health.Tunnel?.Name) ? relay.TunnelName : health.Tunnel.Name,
            TunnelStatus = health.Status,
            LastValidatedAtUtc = DateTimeOffset.UtcNow,
            LastValidationError = health.Success ? string.Empty : health.Summary
        };

    private async Task SaveRelayStatusAsync(
        EdgeGatewayConfiguration configuration,
        EdgeGatewayRelayZone updatedRelay,
        CancellationToken cancellationToken)
    {
        var updatedConfiguration = configuration with
        {
            RelayZones = configuration.RelayZones
                .Select(relay => relay.DomainName.Equals(updatedRelay.DomainName, StringComparison.OrdinalIgnoreCase)
                    ? updatedRelay
                    : relay)
                .OrderBy(relay => relay.DomainName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CloudflareTunnel = configuration.CloudflareTunnel.TunnelId.Equals(updatedRelay.TunnelId, StringComparison.OrdinalIgnoreCase)
                ? configuration.CloudflareTunnel with
                {
                    TunnelName = string.IsNullOrWhiteSpace(updatedRelay.TunnelName)
                        ? configuration.CloudflareTunnel.TunnelName
                        : updatedRelay.TunnelName,
                    IsAuthenticated = IsTunnelHealthy(updatedRelay.TunnelStatus),
                    LastVerifiedAtUtc = updatedRelay.LastValidatedAtUtc
                }
                : configuration.CloudflareTunnel
        };

        await configurationStore.SaveAsync(updatedConfiguration, cancellationToken);
    }

    private async Task<CaddyApplyAttempt> ApplyCaddyConfigurationAsync(
        EdgeGatewayConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var configPath = ResolvePath(options.Value.CaddyConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ".");

        var caddyfile = GenerateCaddyfile(configuration);
        var existing = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(existing))
        {
            await File.WriteAllTextAsync($"{configPath}.bak", existing, cancellationToken);
        }

        await File.WriteAllTextAsync(configPath, caddyfile, cancellationToken);
        _ = await TryRunCommandAsync("caddy", ["fmt", "--overwrite", configPath], cancellationToken);

        var isRunning = await processStatusProbe.IsRunningAsync("(^|/)caddy( |$)", cancellationToken);
        if (!isRunning)
        {
            return new CaddyApplyAttempt(true, $"Wrote Caddy config to {configPath}; Caddy will load it on service start.");
        }

        var reload = await TryRunCommandAsync("caddy", ["reload", "--config", configPath, "--adapter", "caddyfile"], cancellationToken);
        if (reload.Success)
        {
            return new CaddyApplyAttempt(true, "Reloaded Caddy with the generated Edge Gateway config.");
        }

        return new CaddyApplyAttempt(
            false,
            $"Wrote Caddy config to {configPath}, but reload failed: {FirstNonEmpty(reload.StandardError, reload.StandardOutput, reload.Message)}");
    }

    private string GenerateCaddyfile(EdgeGatewayConfiguration configuration)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by Linux Made Sane - Edge Gateway. Do not edit manually.");
        builder.AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    servers {");
        builder.AppendLine("        protocols h1");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"{ResolveCaddySiteAddress()} {{");
        if (ShouldBindCaddyToLoopback())
        {
            builder.AppendLine("    bind 127.0.0.1");
        }

        builder.AppendLine("    route {");
        builder.AppendLine("        encode zstd gzip");
        builder.AppendLine("        respond /health \"ok\" 200");
        builder.AppendLine();

        foreach (var route in configuration.Applications
                     .Where(route => route.IsEnabled)
                     .OrderBy(route => route.PublicHostname, StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(route => NormalizeRoutePathPrefix(route.TargetPathPrefix).Length))
        {
            var hostname = NormalizePublicHostname(route.PublicHostname);
            if (string.IsNullOrWhiteSpace(hostname) ||
                !Uri.TryCreate(route.UpstreamUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            var matcherName = $"edge_route_{route.Id:N}";
            var pathPrefix = NormalizeRoutePathPrefix(route.TargetPathPrefix);
            var allowedSourceRanges = BuildAllowedSourceRanges(route);
            if (!string.IsNullOrWhiteSpace(route.Notes))
            {
                builder.AppendLine($"        # Route: {SanitizeCaddyComment(route.Notes)}");
            }

            if (RequiresLmsAuthentication(route.AccessPolicy))
            {
                var authMatcherName = $"{matcherName}_auth";
                builder.AppendLine($"        @{authMatcherName} {{");
                builder.AppendLine($"            host {hostname}");
                builder.AppendLine("            path /login /login/* /lmshaauth/login /lmshaauth/email-otp /lmshaauth/logout /edge-auth/* /api/passkeys/login/* /api/passkeys/me/* /api/passkeys/register/complete /scripts/passkeys*.js /app*.css /HA.LMS.EdgeGateway*.styles.css /_framework/* /_content/* /images/lms-auth-panel.png /images/lms-logo-192.png /images/lms-splash.png /images/lms-edge-gateway-icon.png /images/lms-ha-login-art.png /favicon.png /favicon.ico");
                builder.AppendLine("        }");
                builder.AppendLine($"        handle @{authMatcherName} {{");
                builder.AppendLine($"            reverse_proxy {BuildForwardAuthUpstream()} {{");
                builder.AppendLine("                header_up Host {host}");
                builder.AppendLine("                header_up X-Forwarded-Proto https");
                builder.AppendLine("            }");
                builder.AppendLine("        }");
                builder.AppendLine();
            }

            if (!IsBlockedAccessPolicy(route.AccessPolicy) && allowedSourceRanges.Count > 0)
            {
                var deniedMatcherName = $"{matcherName}_source_denied";
                builder.AppendLine($"        @{deniedMatcherName} {{");
                builder.AppendLine($"            host {hostname}");
                if (!string.IsNullOrWhiteSpace(pathPrefix))
                {
                    builder.AppendLine($"            path {pathPrefix} {pathPrefix}/*");
                }

                builder.AppendLine($"            not remote_ip {string.Join(' ', allowedSourceRanges)}");
                builder.AppendLine("        }");
                builder.AppendLine($"        respond @{deniedMatcherName} \"Linux Made Sane Edge Gateway source denied.\" 403");
            }

            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                builder.AppendLine($"        @{matcherName} host {hostname}");
            }
            else
            {
                builder.AppendLine($"        @{matcherName} {{");
                builder.AppendLine($"            host {hostname}");
                builder.AppendLine($"            path {pathPrefix} {pathPrefix}/*");
                builder.AppendLine("        }");
            }

            builder.AppendLine($"        handle @{matcherName} {{");
            if (IsBlockedAccessPolicy(route.AccessPolicy))
            {
                builder.AppendLine("            respond \"Linux Made Sane Edge Gateway route is blocked.\" 403");
            }
            else
            {
                if (RequiresLmsAuthentication(route.AccessPolicy))
                {
                    builder.AppendLine($"            forward_auth {BuildForwardAuthUpstream()} {{");
                    builder.AppendLine("                uri /edge-auth/check");
                    builder.AppendLine("                copy_headers X-LMS-User X-LMS-Email X-LMS-Groups");
                    builder.AppendLine("            }");
                    builder.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(pathPrefix))
                {
                    builder.AppendLine($"            uri strip_prefix {pathPrefix}");
                }

                builder.AppendLine($"            reverse_proxy {route.UpstreamUrl.Trim()} {{");
                if (ShouldUsePublicHostHeader(route))
                {
                    builder.AppendLine("                header_up Host {host}");
                }
                else
                {
                    builder.AppendLine("                header_up Host {upstream_hostport}");
                }

                if (ShouldStripForwardedFor(route))
                {
                    builder.AppendLine("                header_up -X-Forwarded-For");
                }

                if (ShouldUsePublicHostHeader(route))
                {
                    builder.AppendLine("                header_up X-Real-IP {remote_host}");
                }

                builder.AppendLine("                header_up X-Forwarded-Host {host}");
                builder.AppendLine("                header_up X-Forwarded-Proto https");
                builder.AppendLine("                header_up X-Forwarded-Port 443");
                if (ShouldSkipUpstreamTlsVerification(route))
                {
                    builder.AppendLine("                transport http {");
                    builder.AppendLine("                    tls_insecure_skip_verify");
                    builder.AppendLine("                }");
                }

                builder.AppendLine("            }");
            }

            builder.AppendLine("        }");
            builder.AppendLine();
        }

        builder.AppendLine($"        respond \"{FallbackCaddyResponse}\" 404");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
        return builder.ToString();
    }

    private static bool IsHomeAssistantRoute(PublishedApplicationDefinition route)
    {
        if (route.Name.Contains("home assistant", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hostname = NormalizePublicHostname(route.PublicHostname);
        if (hostname.StartsWith("hassio.", StringComparison.OrdinalIgnoreCase) ||
            hostname.StartsWith("homeassistant.", StringComparison.OrdinalIgnoreCase) ||
            hostname.StartsWith("home-assistant.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(route.UpstreamUrl, UriKind.Absolute, out var upstream) &&
               upstream.Port == 8123;
    }

    private static bool ShouldUsePublicHostHeader(PublishedApplicationDefinition route)
    {
        if (IsHomeAssistantPublicHttpsUpstream(route))
        {
            return false;
        }

        return route.UsePublicHostHeader ?? IsHomeAssistantRoute(route);
    }

    private static bool IsHomeAssistantPublicHttpsUpstream(PublishedApplicationDefinition route)
    {
        if (!IsHomeAssistantRoute(route) ||
            !Uri.TryCreate(route.UpstreamUrl, UriKind.Absolute, out var upstream))
        {
            return false;
        }

        return upstream.Port != 8123 && !IsLocalOrPrivateTargetHost(upstream.Host);
    }

    private static bool IsLocalOrPrivateTargetHost(string host)
    {
        var normalized = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (normalized is "homeassistant" or "localhost" or "127.0.0.1" or "::1")
        {
            return true;
        }

        if (normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalized, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static bool ShouldStripForwardedFor(PublishedApplicationDefinition route) =>
        route.StripForwardedFor ?? true;

    private static bool ShouldSkipUpstreamTlsVerification(PublishedApplicationDefinition route) =>
        route.SkipUpstreamTlsVerification ?? IsHttpsUpstream(route.UpstreamUrl);

    private static void AddHomeAssistantUpstreamWarnings(
        PublishedApplicationDefinition route,
        ICollection<string> warnings)
    {
        if (IsHomeAssistantPublicHttpsUpstream(route))
        {
            warnings.Add("Home Assistant is targeting another public HTTPS host. Edge Gateway will preserve that upstream Host header, but the internal target http://homeassistant:8123 is usually the cleaner route inside Home Assistant.");
        }
    }

    private async Task RemoveStaleApplicationCloudflareResourcesAsync(
        EdgeGatewayConfiguration currentConfiguration,
        PublishedApplicationDefinition staleApplication,
        List<string> steps,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalizedDomain = GetDomainNameFromPublicHostname(staleApplication.PublicHostname);
            var normalizedHostLabel = GetHostLabelFromPublicHostname(staleApplication.PublicHostname, normalizedDomain);
            var publicHostname = $"{normalizedHostLabel}.{normalizedDomain}";
            var relay = currentConfiguration.RelayZones.FirstOrDefault(item =>
                item.DomainName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
            var remainingRoutesForHostname = currentConfiguration.Applications.Any(route =>
                route.Id != staleApplication.Id &&
                route.PublicHostname.Equals(publicHostname, StringComparison.OrdinalIgnoreCase));
            if (remainingRoutesForHostname)
            {
                steps.Add($"Kept stale Cloudflare DNS and tunnel ingress for {publicHostname} because another path route still uses that hostname.");
                return;
            }

            var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(apiToken) || relay is null || !IsRelayProvisioned(relay))
            {
                warnings.Add($"Could not clean stale Cloudflare DNS/tunnel ingress for {publicHostname}; relay or API token is not available.");
                return;
            }

            var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
            var zone = string.IsNullOrWhiteSpace(zonesResult.Error)
                ? zonesResult.Zones.FirstOrDefault(item => item.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                : null;
            if (zone is null)
            {
                warnings.Add(string.IsNullOrWhiteSpace(zonesResult.Error)
                    ? $"The saved Cloudflare token cannot manage {normalizedDomain}; stale Cloudflare route for {publicHostname} may still exist."
                    : zonesResult.Error);
                return;
            }

            var relayHostname = $"{normalizedHostLabel}.{relay.RelayHostname}";
            var records = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
            var routeRecord = records.FirstOrDefault(record => IsDnsRecordForHostname(record, publicHostname));
            if (routeRecord is not null && IsSameDnsTarget(routeRecord, relayHostname))
            {
                await dnsService.DeleteRecordAsync(apiToken, zone.Id, routeRecord.Id, cancellationToken);
                steps.Add($"Deleted stale route DNS record {routeRecord.Name} -> {routeRecord.Content}.");
            }
            else if (routeRecord is not null)
            {
                warnings.Add($"Left stale DNS record {routeRecord.Name} alone because it points at {routeRecord.Content}, not {relayHostname}.");
            }

            var accountId = string.IsNullOrWhiteSpace(zone.AccountId)
                ? currentConfiguration.CloudflareTunnel.AccountId
                : zone.AccountId;
            if (!string.IsNullOrWhiteSpace(accountId) && !string.IsNullOrWhiteSpace(relay.TunnelId))
            {
                var tunnelConfiguration = await tunnelService.GetConfigurationAsync(apiToken, accountId, relay.TunnelId, cancellationToken);
                await tunnelService.UpdateConfigurationAsync(
                    apiToken,
                    accountId,
                    relay.TunnelId,
                    new CloudflareTunnelConfiguration(RemoveHostnameTunnelRoute(tunnelConfiguration.Routes, publicHostname)),
                    cancellationToken);
                steps.Add($"Removed stale tunnel ingress for {publicHostname}.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Add($"Could not clean stale Cloudflare DNS/tunnel ingress for {staleApplication.PublicHostname}: {exception.Message}");
        }
    }

    private Task<CommandAttempt> TryRestartCloudflaredAsync(CancellationToken cancellationToken) =>
        TryRestartCloudflaredAsync(
            "Restarted the cloudflared service so it can use the new tunnel token.",
            "cloudflared connector token is saved; restart the add-on if the tunnel process does not start automatically.",
            cancellationToken);

    private async Task<CommandAttempt> TryRestartCloudflaredAsync(
        string successMessage,
        string missingServiceMessage,
        CancellationToken cancellationToken)
    {
        var servicePath = ResolveCloudflaredServicePath();

        if (string.IsNullOrWhiteSpace(servicePath))
        {
            return await TryStartCloudflaredDirectlyAsync(missingServiceMessage, cancellationToken);
        }

        var restart = await TryRunCommandAsync("s6-svc", ["-t", servicePath], cancellationToken);
        if (restart.Success)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var start = await TryRunCommandAsync("s6-svc", ["-u", servicePath], cancellationToken);
            if (!start.Success)
            {
                return await TryStartCloudflaredDirectlyAsync(
                    $"cloudflared service restart sent TERM, but s6 could not bring it up: {FirstNonEmpty(start.StandardError, start.StandardOutput, start.Message)}",
                    cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            var running = await processStatusProbe.IsRunningAsync(CloudflaredProcessPattern, cancellationToken);
            if (running)
            {
                return restart with { Message = successMessage };
            }

            return await TryStartCloudflaredDirectlyAsync(
                "cloudflared service was restarted, but no connector process was detected.",
                cancellationToken);
        }

        return await TryStartCloudflaredDirectlyAsync(
            $"cloudflared service restart failed: {FirstNonEmpty(restart.StandardError, restart.StandardOutput, restart.Message)}",
            cancellationToken);
    }

    private static string ResolveCloudflaredServicePath() =>
        Directory.Exists("/run/service/cloudflared")
            ? "/run/service/cloudflared"
            : Directory.Exists("/run/s6-rc/servicedirs/cloudflared")
                ? "/run/s6-rc/servicedirs/cloudflared"
                : string.Empty;

    private async Task<CommandAttempt> TryStartCloudflaredDirectlyAsync(
        string fallbackReason,
        CancellationToken cancellationToken)
    {
        var token = await tokenStore.GetTunnelTokenAsync(cancellationToken);
        var dataRoot = ResolvePath(options.Value.DataRoot);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
        var pidFilePath = Path.Combine(dataRoot, "cloudflared.pid");
        var logFilePath = Path.Combine(dataRoot, "logs", "cloudflared.log");

        if (string.IsNullOrWhiteSpace(token))
        {
            var stopMessage = await StopManagedDirectCloudflaredAsync(pidFilePath, cancellationToken);
            return new CommandAttempt(
                false,
                FirstNonEmpty(stopMessage, fallbackReason),
                string.Empty,
                string.Empty);
        }

        var executablePath = await ResolveCloudflaredExecutableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new CommandAttempt(
                false,
                "cloudflared executable was not found and the managed download fallback could not install it.",
                string.Empty,
                string.Empty);
        }

        await StopManagedDirectCloudflaredAsync(pidFilePath, cancellationToken);

        var tokenFilePath = Path.Combine(dataRoot, "cloudflared-token");
        await File.WriteAllTextAsync(tokenFilePath, token.Trim(), cancellationToken);
        TrySetOwnerOnlyFileMode(tokenFilePath);

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = dataRoot
            };
            foreach (var argument in new[]
                     {
                         "tunnel",
                         "--no-autoupdate",
                         "--logfile",
                         logFilePath,
                         "run",
                         "--token",
                         token.Trim()
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            return new CommandAttempt(
                false,
                $"cloudflared direct start failed after service restart problem ({fallbackReason}): {exception.Message}",
                string.Empty,
                string.Empty);
        }

        if (process is null)
        {
            return new CommandAttempt(false, "cloudflared could not be started.", string.Empty, string.Empty);
        }

        await File.WriteAllTextAsync(pidFilePath, process.Id.ToString(), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        var running = !process.HasExited && await IsProcessRunningAsync(process.Id, cancellationToken);
        if (running)
        {
            return new CommandAttempt(
                true,
                $"Started cloudflared connector directly using {executablePath} because the supervised service was not active: {fallbackReason}",
                string.Empty,
                string.Empty);
        }

        var logTail = await ReadLogTailAsync(logFilePath, cancellationToken);
        var exitMessage = process.HasExited ? $"cloudflared exited with code {process.ExitCode}." : "cloudflared process was not detected.";
        return new CommandAttempt(
            false,
            $"{exitMessage} Log: {FirstNonEmpty(logTail, "no cloudflared log output")}. {fallbackReason}",
            string.Empty,
            string.Empty);
    }

    private async Task<string> ResolveCloudflaredExecutableAsync(
        CancellationToken cancellationToken,
        ICollection<string>? diagnostics = null)
    {
        var configured = options.Value.CloudflaredExecutablePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = ResolvePath(configured);
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            diagnostics?.Add($"Configured cloudflared executable was not found: {configuredPath}");
        }

        foreach (var candidate in new[]
                 {
                     "/usr/local/bin/cloudflared",
                     "/usr/bin/cloudflared",
                     "/tmp/lms-edge-cloudflared",
                     Path.Combine(ResolvePath(options.Value.DataRoot), "tools", "cloudflared")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var lookup = await TryRunCommandAsync("sh", ["-c", "command -v cloudflared || true"], cancellationToken);
        var path = lookup.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return await TryInstallManagedCloudflaredAsync(diagnostics, cancellationToken);
    }

    private async Task<string> TryInstallManagedCloudflaredAsync(
        ICollection<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        var downloadUrl = ResolveCloudflaredDownloadUrl();
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            diagnostics?.Add($"Managed cloudflared install is not supported for {RuntimeInformation.ProcessArchitecture}.");
            return string.Empty;
        }

        var toolsDirectory = Path.Combine(ResolvePath(options.Value.DataRoot), "tools");
        var executablePath = Path.Combine(toolsDirectory, "cloudflared");
        var temporaryPath = $"{executablePath}.download";
        Directory.CreateDirectory(toolsDirectory);

        try
        {
            diagnostics?.Add($"cloudflared executable not found; downloading managed binary from {downloadUrl}.");
            using var response = await CloudflaredDownloadClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                diagnostics?.Add($"Managed cloudflared download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                return string.Empty;
            }

            await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var localStream = File.Create(temporaryPath))
            {
                await remoteStream.CopyToAsync(localStream, cancellationToken);
            }

            File.Move(temporaryPath, executablePath, overwrite: true);
            TrySetExecutableFileMode(executablePath);
            diagnostics?.Add($"Installed managed cloudflared executable at {executablePath}.");
            return executablePath;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteFile(temporaryPath);
            diagnostics?.Add($"Managed cloudflared install failed: {exception.Message}");
            return string.Empty;
        }
    }

    private static string ResolveCloudflaredDownloadUrl() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64",
            Architecture.Arm64 => "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-arm64",
            _ => string.Empty
        };

    private async Task<string> StopManagedDirectCloudflaredAsync(
        string pidFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(pidFilePath))
        {
            return string.Empty;
        }

        var rawPid = await File.ReadAllTextAsync(pidFilePath, cancellationToken);
        if (!int.TryParse(rawPid.Trim(), out var pid))
        {
            File.Delete(pidFilePath);
            return string.Empty;
        }

        if (!IsCloudflaredProcess(pid) || !await IsProcessRunningAsync(pid, cancellationToken))
        {
            File.Delete(pidFilePath);
            return string.Empty;
        }

        _ = await TryRunCommandAsync("kill", [pid.ToString()], cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        if (await IsProcessRunningAsync(pid, cancellationToken))
        {
            _ = await TryRunCommandAsync("kill", ["-9", pid.ToString()], cancellationToken);
        }

        File.Delete(pidFilePath);
        return $"Stopped previous directly started cloudflared connector process {pid}.";
    }

    private static bool IsCloudflaredProcess(int pid)
    {
        try
        {
            var cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
            return cmdline.Contains("cloudflared", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsProcessRunningAsync(int pid, CancellationToken cancellationToken)
    {
        var check = await TryRunCommandAsync("sh", ["-c", "kill -0 \"$LMS_EDGE_PID\""], new Dictionary<string, string>
        {
            ["LMS_EDGE_PID"] = pid.ToString()
        }, cancellationToken);
        return check.Success;
    }

    private static async Task<string> ReadLogTailAsync(string logFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(logFilePath))
        {
            return string.Empty;
        }

        var lines = await File.ReadAllLinesAsync(logFilePath, cancellationToken);
        return string.Join(" ", lines.TakeLast(8)).Trim();
    }

    private async Task<IReadOnlyList<string>> GetCloudflaredDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var token = await tokenStore.GetTunnelTokenAsync(cancellationToken);
        diagnostics.Add(string.IsNullOrWhiteSpace(token)
            ? "cloudflared connector token is not saved."
            : $"cloudflared connector token is saved ({token.Trim().Length} characters).");

        var running = await processStatusProbe.IsRunningAsync(CloudflaredProcessPattern, cancellationToken);
        diagnostics.Add(running
            ? "cloudflared process is running locally."
            : "cloudflared process is not running locally.");

        var pids = await TryRunCommandAsync("pgrep", ["-f", CloudflaredProcessPattern], cancellationToken);
        diagnostics.Add(!string.IsNullOrWhiteSpace(pids.StandardOutput)
            ? $"cloudflared process ids: {string.Join(", ", pids.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}."
            : "cloudflared process ids: none.");

        var servicePath = ResolveCloudflaredServicePath();
        if (!string.IsNullOrWhiteSpace(servicePath))
        {
            var serviceStatus = await TryRunCommandAsync("s6-svstat", [servicePath], cancellationToken);
            diagnostics.Add($"cloudflared supervisor status: {FirstNonEmpty(serviceStatus.StandardOutput, serviceStatus.StandardError, serviceStatus.Message, "unavailable").Trim()}");
        }
        else
        {
            diagnostics.Add("cloudflared supervisor service was not found.");
        }

        var executablePath = await ResolveCloudflaredExecutableAsync(cancellationToken, diagnostics);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            diagnostics.Add("cloudflared executable: not found in configured path, /usr/local/bin, /usr/bin, /tmp, data tools, or PATH.");
        }
        else
        {
            diagnostics.Add($"cloudflared executable: {executablePath}");
            var version = await TryRunCommandAsync(executablePath, ["--version"], cancellationToken);
            diagnostics.Add($"cloudflared version: {FirstNonEmpty(version.StandardOutput, version.StandardError, version.Message, "unavailable").Trim()}");
        }

        var logFilePath = Path.Combine(ResolvePath(options.Value.DataRoot), "logs", "cloudflared.log");
        var logTail = await ReadLogTailAsync(logFilePath, cancellationToken);
        if (File.Exists(logFilePath))
        {
            diagnostics.Add($"cloudflared log updated: {File.GetLastWriteTimeUtc(logFilePath):O} UTC.");
        }

        diagnostics.Add(string.IsNullOrWhiteSpace(logTail)
            ? "cloudflared log tail: no log output captured yet."
            : $"cloudflared log tail: {logTail}");

        return diagnostics;
    }

    private async Task<CommandAttempt> TryRunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await TryRunCommandAsync(fileName, arguments, null, cancellationToken);

    private async Task<CommandAttempt> TryRunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = "/",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach (var item in environment)
                {
                    startInfo.Environment[item.Key] = item.Value;
                }
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new CommandAttempt(false, $"{fileName} could not be started.", string.Empty, string.Empty);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return new CommandAttempt(
                process.ExitCode == 0,
                process.ExitCode == 0 ? string.Empty : $"{fileName} exited with code {process.ExitCode}.",
                stdout,
                stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CommandAttempt(false, exception.Message, string.Empty, string.Empty);
        }
    }

    private static IReadOnlyList<CloudflareTunnelRoute> MergeWildcardTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string wildcardHostname,
        string caddyServiceUrl)
    {
        var routes = existingRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname) &&
                            !route.Hostname.Equals(wildcardHostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(wildcardHostname, caddyServiceUrl, BuildOriginRequest(caddyServiceUrl)));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RemoveWildcardTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string wildcardHostname)
    {
        var routes = existingRoutes
            .Where(route => string.IsNullOrWhiteSpace(route.Hostname) ||
                            !route.Hostname.Equals(wildcardHostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (routes.All(route => !string.IsNullOrWhiteSpace(route.Hostname)))
        {
            routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        }

        return routes;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RemoveHostnameTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string hostname)
    {
        return RemoveHostnameTunnelRoutes(
            existingRoutes,
            new HashSet<string>([hostname.Trim().TrimEnd('.')], StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RemoveHostnameTunnelRoutes(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        ISet<string> hostnames)
    {
        var routes = existingRoutes
            .Where(route => string.IsNullOrWhiteSpace(route.Hostname) ||
                            !hostnames.Contains(route.Hostname.Trim().TrimEnd('.')))
            .ToList();

        if (routes.All(route => !string.IsNullOrWhiteSpace(route.Hostname)))
        {
            routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        }

        return routes;
    }

    private static IReadOnlyList<CloudflareTunnelRoute> RebuildManagedTunnelIngressRoutes(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        EdgeGatewayRelayZone relay,
        IReadOnlyList<PublishedApplicationDefinition> applications,
        string caddyServiceUrl)
    {
        var managedHostnames = applications
            .Select(application => NormalizePublicHostname(application.PublicHostname))
            .Where(hostname => !string.IsNullOrWhiteSpace(hostname))
            .Append(relay.WildcardHostname.Trim().TrimEnd('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var routes = existingRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname) &&
                            !managedHostnames.Contains(route.Hostname.Trim().TrimEnd('.')))
            .ToList();

        foreach (var applicationGroup in applications
                     .GroupBy(application => NormalizePublicHostname(application.PublicHostname), StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var skipUpstreamTlsVerification = applicationGroup.Any(ShouldSkipUpstreamTlsVerification);
            routes.Add(new CloudflareTunnelRoute(
                applicationGroup.Key,
                caddyServiceUrl,
                BuildOriginRequest(caddyServiceUrl, skipUpstreamTlsVerification)));
        }

        routes.Add(new CloudflareTunnelRoute(relay.WildcardHostname, caddyServiceUrl, BuildOriginRequest(caddyServiceUrl)));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static bool TunnelRoutesEqual(
        IReadOnlyList<CloudflareTunnelRoute> left,
        IReadOnlyList<CloudflareTunnelRoute> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!TunnelRoutesEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TunnelRoutesEqual(CloudflareTunnelRoute left, CloudflareTunnelRoute right) =>
        left.Hostname.Trim().TrimEnd('.').Equals(right.Hostname.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
        left.Service.Trim().Equals(right.Service.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.OriginRequest == right.OriginRequest;

    private static IReadOnlyList<CloudflareTunnelRoute> MergeHostnameTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string hostname,
        string caddyServiceUrl,
        bool skipUpstreamTlsVerification)
    {
        var routes = existingRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname) &&
                            !route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(hostname, caddyServiceUrl, BuildOriginRequest(caddyServiceUrl, skipUpstreamTlsVerification)));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static CloudflareOriginRequestSettings BuildOriginRequest(
        string caddyServiceUrl,
        bool skipUpstreamTlsVerification = false)
    {
        if (!Uri.TryCreate(caddyServiceUrl, UriKind.Absolute, out var uri))
        {
            return CloudflareOriginRequestSettings.Default;
        }

        var isLocalHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                           (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
        return CloudflareOriginRequestSettings.Default with { NoTlsVerify = isLocalHttps || skipUpstreamTlsVerification };
    }

    private static CloudflareTunnel? ResolveExistingTunnel(
        EdgeGatewayConfiguration configuration,
        IReadOnlyList<CloudflareTunnel> tunnels,
        string tunnelBaseName)
    {
        if (!string.IsNullOrWhiteSpace(configuration.CloudflareTunnel.TunnelId))
        {
            var configuredTunnel = tunnels.FirstOrDefault(tunnel =>
                !tunnel.IsDeleted &&
                tunnel.Id.Equals(configuration.CloudflareTunnel.TunnelId, StringComparison.OrdinalIgnoreCase));
            if (configuredTunnel is not null)
            {
                return configuredTunnel;
            }
        }

        return tunnels.FirstOrDefault(tunnel =>
            !tunnel.IsDeleted &&
            tunnel.Name.Equals(tunnelBaseName, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildTunnelBaseName(string relayHostname)
    {
        var slug = string.Concat(relayHostname
                .Trim()
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-'))
            .Trim('-');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "relay";
        }

        var prefix = string.IsNullOrWhiteSpace(options.Value.ManagedTunnelNamePrefix)
            ? "linux-made-sane"
            : options.Value.ManagedTunnelNamePrefix.Trim();
        return $"{prefix}-edge-{slug[..Math.Min(slug.Length, 40)]}";
    }

    private static string BuildUniqueTunnelName(string baseName, IReadOnlyList<CloudflareTunnel> existingTunnels)
    {
        var usedNames = existingTunnels
            .Where(tunnel => !tunnel.IsDeleted)
            .Select(tunnel => tunnel.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!usedNames.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static bool IsWildcardRecordForDomain(CloudflareDnsRecord record, string relayHostname)
    {
        var name = (record.Name ?? string.Empty).Trim().TrimEnd('.');
        var wildcard = $"*.{relayHostname}";
        return name.Equals(wildcard, StringComparison.OrdinalIgnoreCase) ||
               name.Equals("*", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDnsRecordForHostname(CloudflareDnsRecord record, string hostname) =>
        (record.Name ?? string.Empty)
        .Trim()
        .TrimEnd('.')
        .Equals(hostname.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameDnsTarget(CloudflareDnsRecord record, string dnsTarget) =>
        record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
        record.Content.Trim().TrimEnd('.').Equals(dnsTarget.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsRelayProvisioned(EdgeGatewayRelayZone relay) =>
        !string.IsNullOrWhiteSpace(relay.TunnelId) &&
        !string.IsNullOrWhiteSpace(relay.DnsTarget) &&
        !string.IsNullOrWhiteSpace(relay.WildcardHostname);

    private static bool IsTunnelHealthy(string status) =>
        NormalizeTunnelStatus(status).Equals(HealthyTunnelStatus, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTunnelStatus(string status) =>
        string.IsNullOrWhiteSpace(status)
            ? "unknown"
            : status.Trim().ToLowerInvariant();

    private static bool IsApplicationForDomain(PublishedApplicationDefinition application, string domainName)
    {
        try
        {
            return GetDomainNameFromPublicHostname(application.PublicHostname)
                .Equals(domainName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHttpsUpstream(string upstreamUrl) =>
        Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRoutePathPrefix(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return string.Empty;
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = $"/{normalized}";
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.Contains('\\', StringComparison.Ordinal) ||
            normalized.Contains(' ', StringComparison.Ordinal) ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains('?', StringComparison.Ordinal) ||
            normalized.Contains('#', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("URL path prefix cannot contain spaces, query strings, fragments, backslashes, or traversal.");
        }

        return normalized;
    }

    private static string NormalizeRouteTextBlock(string? value) =>
        (value ?? string.Empty).Trim();

    private static IReadOnlyList<string> BuildAllowedSourceRanges(PublishedApplicationDefinition route)
    {
        var ranges = new List<string>();
        if (route.AllowLanOnly)
        {
            ranges.Add("private_ranges");
        }

        ranges.AddRange(SplitRouteList(route.AllowKnownIps));
        return ranges
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SplitRouteList(string? value) =>
        (value ?? string.Empty)
            .Split([',', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildRouteUrlDisplay(string hostname, string? pathPrefix)
    {
        var path = NormalizeRoutePathPrefix(pathPrefix);
        return string.IsNullOrWhiteSpace(path)
            ? hostname
            : $"{hostname}{path}";
    }

    private static string BuildPathDisplay(string? pathPrefix)
    {
        var path = NormalizeRoutePathPrefix(pathPrefix);
        return string.IsNullOrWhiteSpace(path) ? "/" : path;
    }

    private static bool IsBlockedAccessPolicy(string? accessPolicy) =>
        (accessPolicy ?? string.Empty).Contains("block", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresLmsAuthentication(string? accessPolicy) =>
        !IsBlockedAccessPolicy(accessPolicy) && !IsPassThroughAccessPolicy(accessPolicy);

    private static bool IsPassThroughAccessPolicy(string? accessPolicy)
    {
        var value = (accessPolicy ?? string.Empty).Trim();
        return value.Equals("Pass Through", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Pass-through", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("PassThrough", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Public", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildForwardAuthUpstream() =>
        $"127.0.0.1:{Math.Clamp(options.Value.LmsForwardAuthPort, 1, 65535)}";

    private static string SanitizeCaddyComment(string value) =>
        string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Replace("#", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static string NormalizeDomainName(string domainName)
    {
        var normalized = (domainName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('*', StringComparison.Ordinal) ||
            normalized.Contains('/', StringComparison.Ordinal) ||
            normalized.Contains('\\', StringComparison.Ordinal) ||
            normalized.Contains(' ', StringComparison.Ordinal) ||
            !normalized.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Select a valid Cloudflare domain before setting up the relay.");
        }

        return normalized;
    }

    private static string NormalizePublicHostname(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        }

        return (value ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant() ?? string.Empty;
    }

    private static string GetDomainNameFromPublicHostname(string publicHostname)
    {
        var hostname = NormalizePublicHostname(publicHostname);
        var parts = hostname.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("The saved public hostname does not contain a valid domain.");
        }

        return NormalizeDomainName($"{parts[^2]}.{parts[^1]}");
    }

    private static string GetHostLabelFromPublicHostname(string publicHostname, string domainName)
    {
        var hostname = NormalizePublicHostname(publicHostname);
        var normalizedDomain = NormalizeDomainName(domainName);
        if (!hostname.EndsWith($".{normalizedDomain}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The saved public hostname is not under {normalizedDomain}.");
        }

        var label = hostname[..^($".{normalizedDomain}".Length)];
        return NormalizeHostLabel(label);
    }

    private static string NormalizeHostLabelForDomain(string value, string domainName)
    {
        var normalizedDomain = NormalizeDomainName(domainName);
        var normalized = (value ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "hassio";
        }

        if (!normalized.Contains('.', StringComparison.Ordinal))
        {
            return NormalizeHostLabel(normalized);
        }

        var hostname = NormalizePublicHostname(normalized);
        var suffix = $".{normalizedDomain}";
        if (!hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Hostname must be a subdomain of {normalizedDomain}.");
        }

        var label = hostname[..^suffix.Length];
        return NormalizeHostLabel(label);
    }

    private static string NormalizeHostLabel(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "hassio";
        }

        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Use a single hostname label, for example hassio, or the matching full hostname for this domain.");
        }

        if (normalized.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidOperationException("App label can contain only letters, numbers, and hyphens.");
        }

        return normalized;
    }

    private static string NormalizeTargetOrigin(string scheme, string host, int port)
    {
        var rawHost = (host ?? string.Empty).Trim();
        if (Uri.TryCreate(rawHost, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Target must use HTTP or HTTPS.");
            }

            if (!string.IsNullOrWhiteSpace(uri.PathAndQuery) && uri.PathAndQuery != "/")
            {
                throw new InvalidOperationException("Enter the target host/container and port only; use URL path prefix for route paths.");
            }

            var parsedPort = uri.IsDefaultPort
                ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
                : uri.Port;
            return FormatTargetOrigin(uri.Scheme, uri.Host, parsedPort);
        }

        var normalizedScheme = string.IsNullOrWhiteSpace(scheme)
            ? Uri.UriSchemeHttp
            : scheme.Trim().ToLowerInvariant();
        if (normalizedScheme != Uri.UriSchemeHttp && normalizedScheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Target scheme must be http or https.");
        }

        if (string.IsNullOrWhiteSpace(rawHost) ||
            rawHost.Contains('/', StringComparison.Ordinal) ||
            rawHost.Contains('\\', StringComparison.Ordinal) ||
            rawHost.Contains(' ', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Enter an internal host, IP address, or Docker service name for the target.");
        }

        return FormatTargetOrigin(normalizedScheme, rawHost.TrimEnd('.'), port);
    }

    private static string FormatTargetOrigin(string scheme, string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Target port must be between 1 and 65535.");
        }

        return new UriBuilder(scheme, host, port)
            .Uri
            .GetLeftPart(UriPartial.Authority)
            .TrimEnd('/');
    }

    private static void TrySetOwnerOnlyFileMode(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Best effort only; Windows/non-Unix file systems do not support Unix modes.
        }
    }

    private static void TrySetExecutableFileMode(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            // Best effort only; Windows/non-Unix file systems do not support Unix modes.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string BuildRelayHostname(string domainName) => $"{RelayNamespace}.{domainName}";

    private string ResolveCaddyServiceUrl() =>
        string.IsNullOrWhiteSpace(options.Value.CaddyLocalServiceUrl)
            ? "http://localhost:18080"
            : options.Value.CaddyLocalServiceUrl.Trim();

    private string ResolveCaddySiteAddress()
    {
        var serviceUrl = ResolveCaddyServiceUrl();
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri))
        {
            return ":18080";
        }

        var port = uri.IsDefaultPort
            ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;
        var scheme = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";
        return $"{scheme}://:{Math.Clamp(port, 1, 65535)}";
    }

    private bool ShouldBindCaddyToLoopback()
    {
        var serviceUrl = ResolveCaddyServiceUrl();
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address))
        {
            return true;
        }

        return false;
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static EdgeGatewayRelayProvisioningResult Failure(
        string summary,
        IReadOnlyList<string> steps,
        IReadOnlyList<string> warnings) =>
        new(false, false, null, summary, steps, warnings);

    private sealed record CommandAttempt(
        bool Success,
        string Message,
        string StandardOutput,
        string StandardError);

    private sealed record CaddyApplyAttempt(bool Success, string Message);

    private sealed record CaddyRouteCheck(bool Success, string Summary);

    private sealed record TunnelHealthCheck(
        bool Success,
        CloudflareTunnel? Tunnel,
        string Status,
        string Summary);
}
