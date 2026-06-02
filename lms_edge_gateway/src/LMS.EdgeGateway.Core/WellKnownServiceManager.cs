using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class WellKnownServiceManager(
    IOptions<EdgeGatewayCoreOptions> options,
    IWellKnownServiceStore store,
    IEdgeGatewayRelayProvisioningService relayProvisioningService,
    HttpClient httpClient,
    IEdgeGatewayConfigurationStore? edgeGatewayConfigurationStore = null,
    ICloudflareApiTokenStore? tokenStore = null,
    ICloudflareZoneService? zoneService = null,
    ICloudflareDnsService? dnsService = null,
    ICloudflareTunnelService? tunnelService = null) : IWellKnownServiceManager
{
    private const string TeslaFleetPath = "/.well-known/appspecific/com.tesla.3p.public-key.pem";

    public IReadOnlyList<WellKnownTemplateDefinition> GetTemplates() =>
    [
        new(
            WellKnownTemplateKind.TeslaFleet,
            "Tesla Fleet",
            "Generate a Tesla Fleet EC key pair and publish only the public key.",
            TeslaFleetPath,
            "application/x-pem-file",
            WellKnownSourceType.Generated,
            string.Empty),
        new(
            WellKnownTemplateKind.SecurityTxt,
            "Security.txt",
            "Publish contact and policy details for responsible disclosure.",
            "/.well-known/security.txt",
            "text/plain; charset=utf-8",
            WellKnownSourceType.StaticText,
            "Contact: mailto:security@example.com\nExpires: 2027-01-01T00:00:00Z\nPreferred-Languages: en\n"),
        new(
            WellKnownTemplateKind.WebFinger,
            "WebFinger",
            "Publish a static WebFinger JSON response. Dynamic query-aware routing can be added later.",
            "/.well-known/webfinger",
            "application/jrd+json",
            WellKnownSourceType.Json,
            "{\n  \"subject\": \"acct:user@example.com\",\n  \"links\": []\n}"),
        new(
            WellKnownTemplateKind.AppleAppSiteAssociation,
            "Apple App Site Association",
            "Publish apple-app-site-association for Universal Links.",
            "/.well-known/apple-app-site-association",
            "application/json",
            WellKnownSourceType.Json,
            "{\n  \"applinks\": {\n    \"apps\": [],\n    \"details\": []\n  }\n}"),
        new(
            WellKnownTemplateKind.AndroidAssetLinks,
            "Android Asset Links",
            "Publish Android assetlinks.json for App Links.",
            "/.well-known/assetlinks.json",
            "application/json",
            WellKnownSourceType.Json,
            "[\n  {\n    \"relation\": [\"delegate_permission/common.handle_all_urls\"],\n    \"target\": {\n      \"namespace\": \"android_app\",\n      \"package_name\": \"com.example.app\",\n      \"sha256_cert_fingerprints\": []\n    }\n  }\n]"),
        new(
            WellKnownTemplateKind.OpenIdConfiguration,
            "OpenID/OAuth Discovery",
            "Publish OpenID/OAuth discovery metadata.",
            "/.well-known/openid-configuration",
            "application/json",
            WellKnownSourceType.Json,
            "{\n  \"issuer\": \"https://example.com\",\n  \"authorization_endpoint\": \"https://example.com/oauth/authorize\",\n  \"token_endpoint\": \"https://example.com/oauth/token\"\n}"),
        new(
            WellKnownTemplateKind.CustomText,
            "Custom .well-known File",
            "Publish a custom text file below /.well-known/.",
            "/.well-known/example.txt",
            "text/plain; charset=utf-8",
            WellKnownSourceType.StaticText,
            string.Empty),
        new(
            WellKnownTemplateKind.CustomJson,
            "Custom .well-known JSON",
            "Publish custom JSON below /.well-known/.",
            "/.well-known/example.json",
            "application/json",
            WellKnownSourceType.Json,
            "{\n}\n")
    ];

    public async Task<WellKnownConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        await store.LoadAsync(cancellationToken);

    public async Task<WellKnownServiceSaveResult> CreateTeslaFleetAsync(
        string domain,
        string displayName = "Tesla Fleet public key",
        CancellationToken cancellationToken = default)
    {
        var serviceId = Guid.NewGuid();
        var secretDirectory = Path.Combine(ResolveDataRoot(), "secrets", "tesla-fleet", serviceId.ToString("N"));
        Directory.CreateDirectory(secretDirectory);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = key.ExportECPrivateKeyPem();
        var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        var privateKeyPath = Path.Combine(secretDirectory, "private-key.pem");
        await File.WriteAllTextAsync(privateKeyPath, privateKeyPem, cancellationToken);
        TrySetOwnerOnly(privateKeyPath);

        var request = new WellKnownServiceSaveRequest(
            serviceId,
            displayName,
            domain,
            TeslaFleetPath,
            "application/x-pem-file",
            publicKeyPem,
            WellKnownSourceType.Generated,
            Template: WellKnownTemplateKind.TeslaFleet.ToString());

        var result = await SaveAsync(request, cancellationToken);
        if (result.Service is null)
        {
            File.Delete(privateKeyPath);
            return result;
        }

        var updated = result.Service with { SecretFilePath = privateKeyPath };
        await UpsertAsync(updated, cancellationToken);
        return result with
        {
            Service = updated,
            Steps = [.. result.Steps, $"Stored Tesla Fleet private key at {privateKeyPath}."]
        };
    }

    public async Task<WellKnownServiceSaveResult> CreateSecurityTxtAsync(
        SecurityTxtTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var domain = WellKnownPath.NormalizeDomain(request.Domain);
        var body = WellKnownContent.BuildSecurityTxt(request with
        {
            CanonicalUrl = string.IsNullOrWhiteSpace(request.CanonicalUrl)
                ? $"https://{domain}/.well-known/security.txt"
                : request.CanonicalUrl
        });

        return await SaveAsync(
            new WellKnownServiceSaveRequest(
                null,
                "Security.txt",
                domain,
                "/.well-known/security.txt",
                "text/plain; charset=utf-8",
                body,
                WellKnownSourceType.StaticText,
                Template: WellKnownTemplateKind.SecurityTxt.ToString()),
            cancellationToken);
    }

    public async Task<WellKnownServiceSaveResult> SaveAsync(
        WellKnownServiceSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var configuration = await store.LoadAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var id = request.Id.GetValueOrDefault();
            var existing = id == Guid.Empty
                ? null
                : configuration.Services.FirstOrDefault(service => service.Id == id);

            id = id == Guid.Empty ? Guid.NewGuid() : id;
            var relativePath = WellKnownPath.NormalizeRelativePath(request.RelativePath);
            var domain = WellKnownPath.NormalizeDomain(request.Domain);
            var contentType = WellKnownPath.NormalizeContentType(request.ContentType, relativePath, request.SourceType);
            WellKnownPath.ValidateContentType(contentType, request.AdvancedContentTypeConfirmed);
            WellKnownPath.ValidatePublicBody(request.Body, request.SensitivePublicBodyConfirmed);

            var body = request.SourceType == WellKnownSourceType.Json
                ? WellKnownContent.FormatJson(request.Body)
                : request.Body ?? string.Empty;

            var duplicate = configuration.Services.FirstOrDefault(service =>
                service.Id != id &&
                service.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                service.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                return new WellKnownServiceSaveResult(
                    false,
                    existing,
                    $"{domain}{relativePath} is already managed by {duplicate.DisplayName}.",
                    steps,
                    warnings);
            }

            var publicFilePath = WellKnownPath.BuildPublicFilePath(
                options.Value.DataRoot,
                options.Value.WellKnownPublicRoot,
                domain,
                relativePath);
            var service = new WellKnownService(
                id,
                string.IsNullOrWhiteSpace(request.DisplayName) ? relativePath : request.DisplayName.Trim(),
                domain,
                relativePath,
                contentType,
                body,
                request.SourceType,
                request.Enabled,
                request.RequiresAuth,
                request.PublicReadOnly,
                existing?.CreatedUtc ?? now,
                now,
                existing?.LastPublishedUtc,
                existing?.LastVerifiedUtc,
                existing?.LastVerificationStatus ?? string.Empty,
                existing?.LastVerificationMessage ?? string.Empty,
                string.IsNullOrWhiteSpace(request.CacheControl) ? "no-store" : request.CacheControl.Trim(),
                request.Template,
                WellKnownPath.BuildPublicUrl(domain, relativePath),
                publicFilePath,
                existing?.SecretFilePath ?? string.Empty,
                request.AdvancedContentTypeConfirmed,
                request.SensitivePublicBodyConfirmed);

            await UpsertAsync(service, cancellationToken);
            await WritePublicFileAsync(service, cancellationToken);
            steps.Add($"Wrote public .well-known file to {service.PublicFilePath}.");
            if (service.Enabled)
            {
                await EnsureCloudflareRouteAsync(service, steps, warnings, cancellationToken);
            }

            var caddyResult = await relayProvisioningService.RefreshCaddyConfigurationAsync(cancellationToken);
            if (caddyResult.Success)
            {
                steps.Add(caddyResult.Summary);
            }
            else
            {
                warnings.Add(caddyResult.Summary);
            }

            var published = service with
            {
                LastPublishedUtc = now,
                LastVerificationStatus = caddyResult.Success ? "Published" : "Caddy warning",
                LastVerificationMessage = caddyResult.Summary
            };
            await UpsertAsync(published, cancellationToken);

            return new WellKnownServiceSaveResult(
                caddyResult.Success,
                published,
                caddyResult.Success ? $"Published {published.PublicUrl}." : $"Saved {published.PublicUrl}, but Caddy did not reload cleanly.",
                steps,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new WellKnownServiceSaveResult(false, null, exception.Message, steps, warnings);
        }
    }

    public async Task<WellKnownServiceSaveResult> PublishAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.LoadAsync(cancellationToken);
        var service = configuration.Services.FirstOrDefault(item => item.Id == serviceId);
        if (service is null)
        {
            return new WellKnownServiceSaveResult(false, null, "The .well-known service no longer exists.", [], []);
        }

        return await SaveAsync(
            new WellKnownServiceSaveRequest(
                service.Id,
                service.DisplayName,
                service.Domain,
                service.RelativePath,
                service.ContentType,
                service.Body,
                service.SourceType,
                service.Enabled,
                service.RequiresAuth,
                service.PublicReadOnly,
                service.CacheControl,
                service.Template,
                service.AdvancedContentTypeConfirmed,
                service.SensitivePublicBodyConfirmed),
            cancellationToken);
    }

    public async Task<WellKnownServiceSaveResult> SetEnabledAsync(
        Guid serviceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.LoadAsync(cancellationToken);
        var service = configuration.Services.FirstOrDefault(item => item.Id == serviceId);
        if (service is null)
        {
            return new WellKnownServiceSaveResult(false, null, "The .well-known service no longer exists.", [], []);
        }

        return await SaveAsync(
            new WellKnownServiceSaveRequest(
                service.Id,
                service.DisplayName,
                service.Domain,
                service.RelativePath,
                service.ContentType,
                service.Body,
                service.SourceType,
                enabled,
                service.RequiresAuth,
                service.PublicReadOnly,
                service.CacheControl,
                service.Template,
                service.AdvancedContentTypeConfirmed,
                service.SensitivePublicBodyConfirmed),
            cancellationToken);
    }

    public async Task<WellKnownDeleteResult> DeleteAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        var configuration = await store.LoadAsync(cancellationToken);
        var service = configuration.Services.FirstOrDefault(item => item.Id == serviceId);
        if (service is null)
        {
            return new WellKnownDeleteResult(true, serviceId, "The .well-known service was already deleted.", steps, warnings);
        }

        await store.SaveAsync(
            configuration with
            {
                Services = configuration.Services.Where(item => item.Id != serviceId).ToArray()
            },
            cancellationToken);

        DeletePublicFileIfUnused(configuration, service);
        steps.Add($"Removed {service.PublicUrl} from the managed service list.");

        var caddyResult = await relayProvisioningService.RefreshCaddyConfigurationAsync(cancellationToken);
        if (caddyResult.Success)
        {
            steps.Add(caddyResult.Summary);
        }
        else
        {
            warnings.Add(caddyResult.Summary);
        }

        return new WellKnownDeleteResult(caddyResult.Success, serviceId, $"Deleted {service.DisplayName}.", steps, warnings);
    }

    public async Task<WellKnownVerificationResult> VerifyAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        var configuration = await store.LoadAsync(cancellationToken);
        var service = configuration.Services.FirstOrDefault(item => item.Id == serviceId);
        if (service is null)
        {
            return new WellKnownVerificationResult(false, serviceId, "Missing", "The .well-known service no longer exists.", [], DateTimeOffset.UtcNow);
        }

        var checks = new List<string>();
        var checkedAt = DateTimeOffset.UtcNow;
        var success = false;
        var status = "Failed";
        var message = string.Empty;

        try
        {
            if (!File.Exists(service.PublicFilePath))
            {
                message = $"Public file does not exist at {service.PublicFilePath}. Rebuild the service before verifying.";
                checks.Add(message);
            }
            else
            {
                checks.Add("Public file exists in the well-known store.");

                var response = await FetchPublicUrlAsync(service.PublicUrl, cancellationToken);
                AddHttpChecks(checks, response, "HTTP");
                if (ShouldAttemptTunnelRepair(response))
                {
                    checks.Add(await TryRepairRelayForHostnameAsync(service.Domain, cancellationToken));
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    response = await FetchPublicUrlAsync(service.PublicUrl, cancellationToken);
                    AddHttpChecks(checks, response, "Retry HTTP");
                }

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    message = "Verification returned a redirect. The route may be protected by auth or pointed at the wrong service.";
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden && LooksLikeCloudflareAccessBlock(response))
                {
                    message = "Verification was blocked by Cloudflare Access.";
                }
                else if (LooksLikeAuthRedirect(response.Body))
                {
                    message = "Verification returned an LMS authentication page instead of public .well-known content.";
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                {
                    message = $"Expected HTTP 200 but received {(int)response.StatusCode}.";
                }
                else if (!IsAcceptableContentType(service, response.ContentType, response.Body))
                {
                    message = $"Content-Type {response.ContentType} was not acceptable for {service.ContentType}.";
                }
                else if (service.Template.Equals(WellKnownTemplateKind.TeslaFleet.ToString(), StringComparison.OrdinalIgnoreCase) &&
                         !response.Body.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
                {
                    message = "The response did not contain a PEM public key.";
                }
                else
                {
                    success = true;
                    status = "Verified";
                    message = $"Verified {service.PublicUrl}.";
                    checks.Add("No auth redirect or Cloudflare Access block was detected.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            message = $"Verification failed: {exception.Message}";
        }

        var updated = service with
        {
            LastVerifiedUtc = checkedAt,
            LastVerificationStatus = status,
            LastVerificationMessage = message
        };
        await UpsertAsync(updated, cancellationToken);
        return new WellKnownVerificationResult(success, serviceId, status, message, checks, checkedAt);
    }

    public string BuildPublicUrl(WellKnownService service) =>
        WellKnownPath.BuildPublicUrl(service.Domain, service.RelativePath);

    private async Task UpsertAsync(WellKnownService service, CancellationToken cancellationToken)
    {
        var configuration = await store.LoadAsync(cancellationToken);
        await store.SaveAsync(
            configuration with
            {
                Services = configuration.Services
                    .Where(item => item.Id != service.Id)
                    .Append(service)
                    .OrderBy(item => item.Domain, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            cancellationToken);
    }

    private async Task WritePublicFileAsync(WellKnownService service, CancellationToken cancellationToken)
    {
        var path = WellKnownPath.BuildPublicFilePath(
            options.Value.DataRoot,
            options.Value.WellKnownPublicRoot,
            service.Domain,
            service.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ResolveDataRoot());
        await File.WriteAllTextAsync(path, service.Body ?? string.Empty, Encoding.UTF8, cancellationToken);
    }

    private static void DeletePublicFileIfUnused(WellKnownConfiguration configuration, WellKnownService service)
    {
        var stillUsed = configuration.Services.Any(item =>
            item.Id != service.Id &&
            item.PublicFilePath.Equals(service.PublicFilePath, StringComparison.Ordinal));
        if (!stillUsed && File.Exists(service.PublicFilePath))
        {
            File.Delete(service.PublicFilePath);
        }
    }

    private string ResolveDataRoot() =>
        Path.IsPathRooted(options.Value.DataRoot)
            ? options.Value.DataRoot
            : Path.GetFullPath(options.Value.DataRoot);

    private async Task EnsureCloudflareRouteAsync(
        WellKnownService service,
        ICollection<string> steps,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (edgeGatewayConfigurationStore is null ||
            tokenStore is null ||
            zoneService is null ||
            dnsService is null ||
            tunnelService is null)
        {
            warnings.Add("Cloudflare DNS/tunnel reconciliation was skipped because Cloudflare services are not available.");
            return;
        }

        var configuration = await edgeGatewayConfigurationStore.LoadAsync(cancellationToken);
        var relay = FindRelayForHostname(configuration, service.Domain);
        if (relay is null ||
            string.IsNullOrWhiteSpace(relay.TunnelId) ||
            string.IsNullOrWhiteSpace(relay.RelayHostname))
        {
            warnings.Add($"No configured relay was found for {service.Domain}; Caddy will be ready but Cloudflare may not route this hostname yet.");
            return;
        }

        var apiToken = await tokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            warnings.Add("Cloudflare API token is not configured; DNS and tunnel ingress were not updated.");
            return;
        }

        var zonesResult = await zoneService.ListZonesAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(zonesResult.Error))
        {
            warnings.Add(zonesResult.Error);
            return;
        }

        var zone = zonesResult.Zones.FirstOrDefault(item =>
            item.Name.Equals(relay.DomainName, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            warnings.Add($"The saved Cloudflare token cannot manage {relay.DomainName}; DNS and tunnel ingress were not updated.");
            return;
        }

        var relayTarget = BuildRelayTargetHostname(service.Domain, relay);
        var records = await dnsService.ListRecordsAsync(apiToken, zone.Id, cancellationToken);
        var record = records.FirstOrDefault(item =>
            item.Name.Equals(service.Domain, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            await dnsService.CreateRecordAsync(
                apiToken,
                zone.Id,
                new CloudflareDnsRecord(
                    string.Empty,
                    zone.Id,
                    service.Domain,
                    "CNAME",
                    relayTarget,
                    true,
                    1,
                    options.Value.ManagedRecordComment,
                    null),
                cancellationToken);
            steps.Add($"Created Cloudflare DNS record {service.Domain} -> {relayTarget}.");
        }
        else if (record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                 record.Content.Equals(relayTarget, StringComparison.OrdinalIgnoreCase) &&
                 record.Proxied)
        {
            steps.Add($"Cloudflare DNS record {service.Domain} already points at {relayTarget}.");
        }
        else if (record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                 IsManagedRecord(record.Comment))
        {
            await dnsService.UpdateRecordAsync(
                apiToken,
                zone.Id,
                record with
                {
                    Content = relayTarget,
                    Proxied = true,
                    Ttl = 1,
                    Comment = options.Value.ManagedRecordComment
                },
                cancellationToken);
            steps.Add($"Updated Cloudflare DNS record {service.Domain} -> {relayTarget}.");
        }
        else
        {
            warnings.Add($"Cloudflare DNS record {service.Domain} already exists and points at {record.Content}; it was left unchanged.");
        }

        var accountId = string.IsNullOrWhiteSpace(zone.AccountId)
            ? configuration.CloudflareTunnel.AccountId
            : zone.AccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            warnings.Add($"Cloudflare account id was not available for {service.Domain}; tunnel ingress was not updated.");
            return;
        }

        var tunnelConfiguration = await tunnelService.GetConfigurationAsync(apiToken, accountId, relay.TunnelId, cancellationToken);
        var caddyServiceUrl = ResolveCaddyServiceUrl();
        var updatedRoutes = MergeTunnelRoute(tunnelConfiguration.Routes, service.Domain, caddyServiceUrl);
        if (!TunnelRoutesEqual(tunnelConfiguration.Routes, updatedRoutes))
        {
            await tunnelService.UpdateConfigurationAsync(
                apiToken,
                accountId,
                relay.TunnelId,
                new CloudflareTunnelConfiguration(updatedRoutes),
                cancellationToken);
            steps.Add($"Configured Cloudflare tunnel ingress {service.Domain} -> {caddyServiceUrl}.");
        }
        else
        {
            steps.Add($"Cloudflare tunnel ingress for {service.Domain} is already configured.");
        }
    }

    private static EdgeGatewayRelayZone? FindRelayForHostname(
        EdgeGatewayConfiguration configuration,
        string hostname) =>
        configuration.RelayZones
            .Where(relay => hostname.Equals(relay.DomainName, StringComparison.OrdinalIgnoreCase) ||
                            hostname.EndsWith($".{relay.DomainName}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(relay => relay.DomainName.Length)
            .FirstOrDefault();

    private static string BuildRelayTargetHostname(string hostname, EdgeGatewayRelayZone relay)
    {
        if (hostname.Equals(relay.DomainName, StringComparison.OrdinalIgnoreCase))
        {
            return relay.RelayHostname;
        }

        var suffix = $".{relay.DomainName}";
        var label = hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? hostname[..^suffix.Length].Trim('.')
            : hostname;
        return $"{label}.{relay.RelayHostname}";
    }

    private static IReadOnlyList<CloudflareTunnelRoute> MergeTunnelRoute(
        IReadOnlyList<CloudflareTunnelRoute> existingRoutes,
        string hostname,
        string caddyServiceUrl)
    {
        var routes = existingRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Hostname) &&
                            !route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Add(new CloudflareTunnelRoute(hostname, caddyServiceUrl, BuildOriginRequest(caddyServiceUrl)));
        routes.Add(new CloudflareTunnelRoute(string.Empty, "http_status:404"));
        return routes;
    }

    private static CloudflareOriginRequestSettings BuildOriginRequest(string caddyServiceUrl)
    {
        if (!Uri.TryCreate(caddyServiceUrl, UriKind.Absolute, out var uri))
        {
            return CloudflareOriginRequestSettings.Default;
        }

        var localHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                         (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
        return CloudflareOriginRequestSettings.Default with { NoTlsVerify = localHttps };
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
            if (!left[index].Hostname.Trim().TrimEnd('.').Equals(right[index].Hostname.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
                !left[index].Service.Trim().Equals(right[index].Service.Trim(), StringComparison.OrdinalIgnoreCase) ||
                left[index].OriginRequest != right[index].OriginRequest)
            {
                return false;
            }
        }

        return true;
    }

    private string ResolveCaddyServiceUrl() =>
        string.IsNullOrWhiteSpace(options.Value.CaddyLocalServiceUrl)
            ? "http://localhost:18080"
            : options.Value.CaddyLocalServiceUrl.TrimEnd('/');

    private bool IsManagedRecord(string? comment) =>
        !string.IsNullOrWhiteSpace(comment) &&
        comment.Contains(options.Value.ManagedRecordComment, StringComparison.OrdinalIgnoreCase);

    private async Task<WellKnownHttpVerificationResponse> FetchPublicUrlAsync(
        string publicUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, publicUrl);
        request.Headers.UserAgent.ParseAdd("LinuxMadeSane-edge-well-known-verifier");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var hasCloudflareAccessHeader = response.Headers.Any(header =>
            header.Key.Contains("cf-access", StringComparison.OrdinalIgnoreCase));

        return new WellKnownHttpVerificationResponse(
            response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            contentType,
            body,
            hasCloudflareAccessHeader);
    }

    private static void AddHttpChecks(
        ICollection<string> checks,
        WellKnownHttpVerificationResponse response,
        string label)
    {
        checks.Add($"{label} {(int)response.StatusCode} {response.ReasonPhrase}.");
        checks.Add(string.IsNullOrWhiteSpace(response.ContentType)
            ? $"{label}: no Content-Type header returned."
            : $"{label} Content-Type: {response.ContentType}.");
    }

    private static bool ShouldAttemptTunnelRepair(WellKnownHttpVerificationResponse response) =>
        response.StatusCode == HttpStatusCode.BadGateway ||
        (int)response.StatusCode == 530 ||
        response.Body.Contains("error code: 1033", StringComparison.OrdinalIgnoreCase) ||
        response.Body.Contains("Cloudflare Tunnel error", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelayProvisioned(EdgeGatewayRelayZone relay) =>
        !string.IsNullOrWhiteSpace(relay.TunnelId) &&
        !string.IsNullOrWhiteSpace(relay.DnsTarget) &&
        !string.IsNullOrWhiteSpace(relay.WildcardHostname);

    private async Task<string> TryRepairRelayForHostnameAsync(
        string hostname,
        CancellationToken cancellationToken)
    {
        if (edgeGatewayConfigurationStore is null)
        {
            return "Cloudflare tunnel repair was skipped because Edge Gateway configuration is not available.";
        }

        var normalizedHostname = WellKnownPath.NormalizeDomain(hostname);
        var configuration = await edgeGatewayConfigurationStore.LoadAsync(cancellationToken);
        var relay = configuration.RelayZones
            .Where(IsRelayProvisioned)
            .OrderByDescending(item => item.DomainName.Length)
            .FirstOrDefault(item =>
                normalizedHostname.Equals(item.DomainName, StringComparison.OrdinalIgnoreCase) ||
                normalizedHostname.EndsWith($".{item.DomainName}", StringComparison.OrdinalIgnoreCase));

        if (relay is null)
        {
            return $"Cloudflare tunnel repair was skipped because no managed relay matched {normalizedHostname}.";
        }

        var repair = await relayProvisioningService.RepairRelayAsync(relay.DomainName, cancellationToken);
        var detail = string.Join(" ", repair.Steps.Concat(repair.Warnings));
        return string.IsNullOrWhiteSpace(detail)
            ? $"Cloudflare tunnel repair for {relay.DomainName}: {repair.Summary}"
            : $"Cloudflare tunnel repair for {relay.DomainName}: {repair.Summary} {detail}";
    }

    private static bool IsAcceptableContentType(WellKnownService service, string actualContentType, string body)
    {
        if (service.Template.Equals(WellKnownTemplateKind.TeslaFleet.ToString(), StringComparison.OrdinalIgnoreCase) &&
            body.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(actualContentType) ||
                   actualContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ||
                   actualContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                   actualContentType.StartsWith("application/x-pem-file", StringComparison.OrdinalIgnoreCase);
        }

        var expected = service.ContentType.Split(';', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(expected) ||
               string.IsNullOrWhiteSpace(actualContentType) ||
               actualContentType.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               actualContentType.StartsWith(expected + ";", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAuthRedirect(string body) =>
        body.Contains("Sign in to LMS Edge Gateway", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("/lmshaauth/login", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("MFA/passkey", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCloudflareAccessBlock(WellKnownHttpVerificationResponse response) =>
        response.HasCloudflareAccessHeader ||
        response.Body.Contains("Cloudflare Access", StringComparison.OrdinalIgnoreCase) ||
        response.Body.Contains("cloudflareaccess.com", StringComparison.OrdinalIgnoreCase);

    private static void TrySetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort hardening; the add-on data directory still owns the file.
        }
    }

    private sealed record WellKnownHttpVerificationResponse(
        HttpStatusCode StatusCode,
        string ReasonPhrase,
        string ContentType,
        string Body,
        bool HasCloudflareAccessHeader);
}
