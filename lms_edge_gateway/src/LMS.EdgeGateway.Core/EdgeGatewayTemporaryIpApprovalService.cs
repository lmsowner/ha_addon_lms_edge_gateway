using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayTemporaryIpApprovalService(
    IEdgeGatewayTemporaryIpApprovalStore approvalStore,
    IEdgeGatewayConfigurationStore configurationStore,
    IEdgeGatewaySecurityStore securityStore,
    IEdgeGatewayEmailDeliveryService emailDeliveryService,
    IOptions<EdgeGatewayCoreOptions> options,
    ILogger<EdgeGatewayTemporaryIpApprovalService> logger) : IEdgeGatewayTemporaryIpApprovalService
{
    private readonly SemaphoreSlim sync = new(1, 1);

    public async Task<TemporaryIpApprovalEvaluationResult> EvaluateAsync(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.SourceIp))
        {
            return new TemporaryIpApprovalEvaluationResult(false, "Temporary IP approval could not resolve the source IP.");
        }

        var countryValidation = ValidateAllowedCountry(route, context.CountryCode);
        if (!countryValidation.Allowed)
        {
            return new TemporaryIpApprovalEvaluationResult(false, countryValidation.Message);
        }

        var now = DateTimeOffset.UtcNow;
        await sync.WaitAsync(cancellationToken);
        try
        {
            var state = CleanExpired(await approvalStore.LoadAsync(cancellationToken), now);
            var grants = state.Grants.ToList();
            var activeGrant = grants.FirstOrDefault(grant => IsSameRouteAndIp(grant, route.Id, context.SourceIp));
            if (activeGrant is not null)
            {
                var touchedGrant = TouchGrantIfNeeded(activeGrant, route, context, now);
                if (!ReferenceEquals(touchedGrant, activeGrant))
                {
                    grants[grants.FindIndex(grant => grant.Id == activeGrant.Id)] = touchedGrant;
                    await approvalStore.SaveAsync(state with { Grants = grants }, cancellationToken);
                }

                return new TemporaryIpApprovalEvaluationResult(true, "Temporary IP approval allowed.");
            }

            var requests = state.Requests.ToList();
            var pending = requests.FirstOrDefault(request => IsSameRouteAndIp(request, route.Id, context.SourceIp));
            if (pending is not null &&
                pending.LastEmailSentUtc is not null &&
                pending.LastEmailSentUtc.Value.Add(GetEmailCooldown()) > now)
            {
                await approvalStore.SaveAsync(state with { Requests = requests, Grants = grants }, cancellationToken);
                return new TemporaryIpApprovalEvaluationResult(
                    false,
                    "Temporary IP approval is pending. An approval email was sent recently; retry after approval.");
            }

            if (pending is not null &&
                pending.EmailSendCount >= GetMaxEmailsPerDay() &&
                pending.CreatedUtc.AddDays(1) > now)
            {
                await approvalStore.SaveAsync(state with { Requests = requests, Grants = grants }, cancellationToken);
                return new TemporaryIpApprovalEvaluationResult(
                    false,
                    "Temporary IP approval email limit reached for this app and IP today.");
            }

            var eligibleRecipients = await ResolveApprovalRecipientsAsync(route, cancellationToken);
            if (eligibleRecipients.Count == 0)
            {
                await approvalStore.SaveAsync(state with { Requests = requests, Grants = grants }, cancellationToken);
                return new TemporaryIpApprovalEvaluationResult(
                    false,
                    "Temporary IP approval needs at least one enabled LMS user or route allowed-user email.");
            }

            var token = CreateToken();
            var tokenHash = HashToken(token);
            var updatedRequest = BuildRequest(route, context, pending, tokenHash, now);
            var emailResult = await SendApprovalEmailsAsync(route, context, token, eligibleRecipients, cancellationToken);
            updatedRequest = updatedRequest with
            {
                LastEmailStatus = emailResult.Message,
                LastEmailSentUtc = now,
                EmailSendCount = updatedRequest.EmailSendCount + 1,
                UpdatedUtc = now
            };

            if (pending is null)
            {
                requests.Add(updatedRequest);
            }
            else
            {
                requests[requests.FindIndex(request => request.Id == pending.Id)] = updatedRequest;
            }

            await approvalStore.SaveAsync(state with { Requests = requests, Grants = grants }, cancellationToken);

            return new TemporaryIpApprovalEvaluationResult(
                false,
                emailResult.Success
                    ? "Temporary IP approval email sent. Retry after approving the request."
                    : $"Temporary IP approval email could not be sent: {emailResult.Message}",
                EmailAttempted: true,
                EmailSucceeded: emailResult.Success);
        }
        finally
        {
            sync.Release();
        }
    }

    public async Task<TemporaryIpApprovalCompletionResult> ApproveAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return new TemporaryIpApprovalCompletionResult(false, "Approval link is incomplete", "The approval token was missing.");
        }

        var tokenHash = HashToken(normalizedToken);
        var now = DateTimeOffset.UtcNow;
        await sync.WaitAsync(cancellationToken);
        try
        {
            var state = CleanExpired(await approvalStore.LoadAsync(cancellationToken), now);
            var requests = state.Requests.ToList();
            var request = requests.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.ApprovalTokenHash) &&
                candidate.ApprovalTokenHash.Equals(tokenHash, StringComparison.Ordinal));
            if (request is null)
            {
                await approvalStore.SaveAsync(state, cancellationToken);
                return new TemporaryIpApprovalCompletionResult(false, "Approval link is not valid", "This approval link is unknown, expired, or already used.");
            }

            if (request.ApprovalTokenExpiresAtUtc is null || request.ApprovalTokenExpiresAtUtc <= now)
            {
                requests[requests.FindIndex(candidate => candidate.Id == request.Id)] = request with
                {
                    ApprovalTokenHash = string.Empty,
                    UpdatedUtc = now,
                    LastEmailStatus = "Approval token expired."
                };
                await approvalStore.SaveAsync(state with { Requests = requests }, cancellationToken);
                return new TemporaryIpApprovalCompletionResult(false, "Approval link expired", "Open the app again to request a fresh approval email.");
            }

            var configuration = await configurationStore.LoadAsync(cancellationToken);
            var route = configuration.Applications.FirstOrDefault(candidate =>
                candidate.Id == request.RouteId &&
                candidate.IsEnabled &&
                EdgeGatewayAccessPolicies.IsTemporaryIpApproval(candidate.AccessPolicy));
            if (route is null)
            {
                return new TemporaryIpApprovalCompletionResult(false, "Route is no longer available", "The published app was disabled or changed before approval completed.");
            }

            var grant = new TemporaryIpApprovalGrant(
                Guid.NewGuid(),
                request.RouteId,
                request.RouteName,
                request.PublicHostname,
                request.TargetPathPrefix,
                request.SourceIp,
                request.CountryCode,
                request.UserAgent,
                now,
                now,
                now.Add(GetIdleTimeout(route)),
                now.Add(GetMaxLifetime(route)));

            var grants = state.Grants
                .Where(candidate => !IsSameRouteAndIp(candidate, request.RouteId, request.SourceIp))
                .Append(grant)
                .ToArray();
            requests[requests.FindIndex(candidate => candidate.Id == request.Id)] = request with
            {
                ApprovalTokenHash = string.Empty,
                ApprovedUtc = now,
                UpdatedUtc = now,
                LastEmailStatus = "Approved."
            };

            await approvalStore.SaveAsync(state with { Requests = requests, Grants = grants }, cancellationToken);
            logger.LogInformation(
                "Audit event: temporary IP approval granted for route {RouteName} {PublicHostname} from {SourceIp}.",
                route.Name,
                route.PublicHostname,
                request.SourceIp);

            return new TemporaryIpApprovalCompletionResult(
                true,
                "Temporary access approved",
                $"Access from {request.SourceIp} is approved for {route.Name}. Reopen the app from the same network.",
                request.SourceIp,
                request.CountryCode,
                route.Name,
                route.PublicHostname,
                BuildRequestedUrl(route.PublicHostname, route.TargetPathPrefix),
                grant.IdleExpiresAtUtc,
                grant.ExpiresAtUtc);
        }
        finally
        {
            sync.Release();
        }
    }

    private TemporaryIpApprovalRequest BuildRequest(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        TemporaryIpApprovalRequest? existing,
        string tokenHash,
        DateTimeOffset now)
    {
        var tokenExpiresAt = now.Add(GetTokenLifetime());
        if (existing is null || existing.CreatedUtc.AddDays(1) <= now)
        {
            return new TemporaryIpApprovalRequest(
                Guid.NewGuid(),
                route.Id,
                route.Name,
                route.PublicHostname,
                route.TargetPathPrefix,
                context.SourceIp,
                NormalizeCountryCode(context.CountryCode),
                context.UserAgent,
                context.RequestedUrl,
                now,
                now,
                null,
                0,
                tokenHash,
                tokenExpiresAt,
                null,
                string.Empty);
        }

        return existing with
        {
            RouteName = route.Name,
            PublicHostname = route.PublicHostname,
            TargetPathPrefix = route.TargetPathPrefix,
            CountryCode = NormalizeCountryCode(context.CountryCode),
            UserAgent = context.UserAgent,
            RequestedUrl = context.RequestedUrl,
            ApprovalTokenHash = tokenHash,
            ApprovalTokenExpiresAtUtc = tokenExpiresAt,
            UpdatedUtc = now
        };
    }

    private TemporaryIpApprovalGrant TouchGrantIfNeeded(
        TemporaryIpApprovalGrant grant,
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        DateTimeOffset now)
    {
        if (grant.LastSeenUtc.AddSeconds(60) > now)
        {
            return grant;
        }

        return grant with
        {
            CountryCode = string.IsNullOrWhiteSpace(context.CountryCode) ? grant.CountryCode : NormalizeCountryCode(context.CountryCode),
            UserAgent = string.IsNullOrWhiteSpace(context.UserAgent) ? grant.UserAgent : context.UserAgent,
            LastSeenUtc = now,
            IdleExpiresAtUtc = now.Add(GetIdleTimeout(route))
        };
    }

    private TemporaryIpApprovalConfiguration CleanExpired(
        TemporaryIpApprovalConfiguration state,
        DateTimeOffset now)
    {
        var requests = state.Requests
            .Where(request =>
                request.CreatedUtc.AddDays(1) > now ||
                request.ApprovedUtc is not null && request.ApprovedUtc.Value.AddDays(1) > now)
            .ToArray();
        var grants = state.Grants
            .Where(grant => grant.ExpiresAtUtc > now && grant.IdleExpiresAtUtc > now)
            .ToArray();
        return state with { Requests = requests, Grants = grants };
    }

    private async Task<IReadOnlyList<string>> ResolveApprovalRecipientsAsync(
        PublishedApplicationDefinition route,
        CancellationToken cancellationToken)
    {
        var configuredUsers = (await securityStore.LoadAsync(cancellationToken)).Users
            .Where(user => user.IsEnabled)
            .Select(user => user.Email)
            .Where(IsValidEmail)
            .ToArray();
        var configuredUserSet = configuredUsers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explicitRecipients = SplitRouteList(route.TemporaryIpApprovalRecipients)
            .Where(IsValidEmail)
            .Where(configuredUserSet.Contains)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(route.TemporaryIpApprovalRecipients))
        {
            return explicitRecipients
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(email => email, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var routeUsers = SplitRouteList(route.AllowedUsers)
            .Where(IsValidEmail)
            .Where(configuredUserSet.Contains)
            .ToArray();

        var recipients = routeUsers.Length > 0
            ? routeUsers
            : configuredUsers;
        return recipients
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(email => email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CountryValidationResult ValidateAllowedCountry(
        PublishedApplicationDefinition route,
        string countryCode)
    {
        var allowedCountries = SplitCountryCodes(route.TemporaryIpApprovalAllowedCountryCodes).ToArray();
        if (allowedCountries.Length == 0)
        {
            return new CountryValidationResult(true, string.Empty);
        }

        var normalized = NormalizeCountryCode(countryCode);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new CountryValidationResult(
                false,
                $"Temporary IP approval only allows countries {string.Join(", ", allowedCountries)}, but Cloudflare did not provide a country code.");
        }

        return allowedCountries.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? new CountryValidationResult(true, string.Empty)
            : new CountryValidationResult(
                false,
                $"Temporary IP approval does not allow requests from {normalized}. Allowed countries: {string.Join(", ", allowedCountries)}.");
    }

    private async Task<ApprovalEmailResult> SendApprovalEmailsAsync(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        string token,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        var approvalUrl = BuildApprovalUrl(route.PublicHostname, token);
        var successes = 0;
        var messages = new List<string>();

        foreach (var recipient in recipients)
        {
            var result = await emailDeliveryService.SendAsync(
                new EmailMessage(
                    string.Empty,
                    "Linux Made Sane",
                    recipient,
                    string.Empty,
                    $"Approve access to {route.Name}",
                    BuildApprovalEmailText(route, context, approvalUrl),
                    BuildApprovalEmailHtml(route, context, approvalUrl)),
                cancellationToken);
            messages.Add(result.Success
                ? $"{recipient}: sent"
                : $"{recipient}: {result.ErrorMessage}");
            if (result.Success)
            {
                successes++;
            }
        }

        return new ApprovalEmailResult(
            successes > 0,
            successes > 0
                ? $"Approval email sent to {successes} recipient(s)."
                : string.Join(" ", messages));
    }

    private string BuildApprovalEmailText(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        string approvalUrl) =>
        $"""
        Linux Made Sane - Edge Gateway

        Access request for {route.Name}

        App: {BuildRequestedUrl(route.PublicHostname, route.TargetPathPrefix)}
        Source IP: {context.SourceIp}
        Approx country: {FormatCountry(context.CountryCode)}
        User agent: {context.UserAgent}

        Approve this IP:
        {approvalUrl}

        Approval is limited to this app and source IP. It expires after {GetIdleTimeout(route).TotalMinutes:0} minutes without traffic, or after {GetMaxLifetime(route).TotalMinutes:0} minutes at most.
        """;

    private string BuildApprovalEmailHtml(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        string approvalUrl)
    {
        var encodedRouteName = WebUtility.HtmlEncode(route.Name);
        var encodedAppUrl = WebUtility.HtmlEncode(BuildRequestedUrl(route.PublicHostname, route.TargetPathPrefix));
        var encodedSourceIp = WebUtility.HtmlEncode(context.SourceIp);
        var encodedCountry = WebUtility.HtmlEncode(FormatCountry(context.CountryCode));
        var encodedUserAgent = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(context.UserAgent) ? "Unknown" : context.UserAgent);
        var encodedApprovalUrl = WebUtility.HtmlEncode(approvalUrl);
        return $$"""
            <!doctype html>
            <html lang="en">
            <body style="margin:0;background:#f3f6fb;color:#142033;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="padding:28px 14px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:620px;background:#ffffff;border:1px solid #dce7f3;border-radius:18px;overflow:hidden;">
                      <tr>
                        <td style="padding:22px 24px;background:#102033;color:#ffffff;">
                          <div style="font-size:12px;font-weight:900;text-transform:uppercase;letter-spacing:.08em;color:#9fd5ff;">Linux Made Sane - Edge Gateway</div>
                          <div style="font-size:22px;font-weight:900;margin-top:6px;">Approve temporary access?</div>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:24px;">
                          <p style="font-size:16px;line-height:1.5;margin:0 0 18px;">A client is trying to reach <strong>{{encodedRouteName}}</strong>. Edge Gateway has denied it until you approve the source IP.</p>
                          {{BuildEmailRow("App", encodedAppUrl)}}
                          {{BuildEmailRow("Source IP", encodedSourceIp)}}
                          {{BuildEmailRow("Approx country", encodedCountry)}}
                          {{BuildEmailRow("Client", encodedUserAgent)}}
                          <table role="presentation" cellspacing="0" cellpadding="0" style="margin:24px 0 18px;">
                            <tr>
                              <td style="background:#0f7b57;border-radius:12px;">
                                <a href="{{encodedApprovalUrl}}" style="display:inline-block;color:#ffffff;text-decoration:none;padding:14px 20px;font-weight:900;font-size:15px;">Approve this IP</a>
                              </td>
                            </tr>
                          </table>
                          <p style="color:#607089;font-size:13px;line-height:1.5;margin:0;">Approval is limited to this app and source IP. It expires after {{GetIdleTimeout(route).TotalMinutes:0}} minutes without traffic, or after {{GetMaxLifetime(route).TotalMinutes:0}} minutes at most.</p>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string BuildEmailRow(string label, string value) =>
        $$"""
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 10px;">
            <tr>
              <td style="padding:12px 14px;background:#f7f9fc;border:1px solid #e3edf7;border-radius:12px;">
                <div style="font-size:11px;color:#607089;font-weight:900;text-transform:uppercase;letter-spacing:.08em;">{{label}}</div>
                <div style="font-size:15px;color:#142033;font-weight:800;margin-top:4px;word-break:break-word;">{{value}}</div>
              </td>
            </tr>
          </table>
        """;

    private TimeSpan GetIdleTimeout(PublishedApplicationDefinition route) =>
        TimeSpan.FromMinutes(Math.Clamp(
            route.TemporaryIpApprovalIdleTimeoutMinutes ?? options.Value.TemporaryIpApprovalIdleTimeoutMinutes,
            1,
            1440));

    private TimeSpan GetMaxLifetime(PublishedApplicationDefinition route) =>
        TimeSpan.FromMinutes(Math.Clamp(
            route.TemporaryIpApprovalMaxLifetimeMinutes ?? options.Value.TemporaryIpApprovalMaxLifetimeMinutes,
            1,
            10080));

    private TimeSpan GetTokenLifetime() =>
        TimeSpan.FromMinutes(Math.Clamp(options.Value.TemporaryIpApprovalTokenLifetimeMinutes, 5, 1440));

    private TimeSpan GetEmailCooldown() =>
        TimeSpan.FromMinutes(Math.Clamp(options.Value.TemporaryIpApprovalEmailCooldownMinutes, 1, 1440));

    private int GetMaxEmailsPerDay() =>
        Math.Clamp(options.Value.TemporaryIpApprovalMaxEmailsPerDay, 1, 100);

    private static string BuildApprovalUrl(string publicHostname, string token) =>
        $"https://{NormalizeHostname(publicHostname)}/edge-auth/approve-ip?token={Uri.EscapeDataString(token)}";

    private static string BuildRequestedUrl(string publicHostname, string? pathPrefix)
    {
        var path = NormalizeRoutePathPrefix(pathPrefix);
        return string.IsNullOrWhiteSpace(path)
            ? $"https://{NormalizeHostname(publicHostname)}"
            : $"https://{NormalizeHostname(publicHostname)}{path}";
    }

    private static string NormalizeHostname(string value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizeRoutePathPrefix(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return string.Empty;
        }

        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized.TrimEnd('/')
            : $"/{normalized.TrimEnd('/')}";
    }

    private static string NormalizeCountryCode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 2 ? normalized : string.Empty;
    }

    private static string FormatCountry(string? countryCode)
    {
        var normalized = NormalizeCountryCode(countryCode);
        return string.IsNullOrWhiteSpace(normalized)
            ? "Unknown"
            : $"{normalized} (from Cloudflare)";
    }

    private static bool IsSameRouteAndIp(TemporaryIpApprovalGrant grant, Guid routeId, string sourceIp) =>
        grant.RouteId == routeId &&
        grant.SourceIp.Equals(sourceIp, StringComparison.OrdinalIgnoreCase);

    private static bool IsSameRouteAndIp(TemporaryIpApprovalRequest request, Guid routeId, string sourceIp) =>
        request.RouteId == routeId &&
        request.SourceIp.Equals(sourceIp, StringComparison.OrdinalIgnoreCase);

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(bytes);
    }

    private static bool IsValidEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        MailAddress.TryCreate(value.Trim(), out _);

    private static IEnumerable<string> SplitRouteList(string? value) =>
        (value ?? string.Empty)
            .Split([',', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> SplitCountryCodes(string? value) =>
        (value ?? string.Empty)
            .Split([',', '\r', '\n', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeCountryCode)
            .Where(country => country.Length == 2 && country.All(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(country => country, StringComparer.OrdinalIgnoreCase);

    private sealed record ApprovalEmailResult(bool Success, string Message);

    private sealed record CountryValidationResult(bool Allowed, string Message);
}
