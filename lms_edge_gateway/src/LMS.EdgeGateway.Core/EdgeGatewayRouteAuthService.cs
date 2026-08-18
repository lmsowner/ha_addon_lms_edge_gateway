using System.Net;
using System.Net.Sockets;
using System.Security.Claims;

namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayRouteAuthService
{
    Task<EdgeGatewayAuthCheckResult> EvaluateAuthAsync(
        EdgeGatewayAuthCheckContext context,
        CancellationToken cancellationToken = default);

    Task<string> BuildSafeReturnPathAsync(string targetUrl, CancellationToken cancellationToken = default);

    Task<bool> IsSafeReturnTargetAsync(string targetUrl, CancellationToken cancellationToken = default);
}

public sealed class EdgeGatewayRouteAuthService(
    IEdgeGatewayConfigurationStore configurationStore,
    IEdgeGatewayAccessCheckPageStore accessCheckPageStore,
    IEdgeGatewayTemporaryIpApprovalService? temporaryIpApprovalService = null,
    ILanClientTrustService? lanClientTrustService = null) : IEdgeGatewayRouteAuthService
{
    private const int StatusOk = 200;
    private const int StatusFound = 302;
    private const int StatusForbidden = 403;
    private const int StatusNotFound = 404;

    public async Task<EdgeGatewayAuthCheckResult> EvaluateAuthAsync(
        EdgeGatewayAuthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var requestedHost = NormalizeForwardedHost(context.ForwardedHost, context.Host);
        var requestedPath = string.IsNullOrWhiteSpace(context.ForwardedUri) ? "/" : context.ForwardedUri.Trim();
        var sourceIp = ResolveSourceIp(context.ConnectingIp, context.ForwardedFor, context.RemoteIpAddress);
        var userEmail = FindFirstValue(context.User, ClaimTypes.Email) ??
                        FindFirstValue(context.User, ClaimTypes.Name) ??
                        string.Empty;

        if (!IsTrustedAuthProxy(context.RemoteIpAddress))
        {
            return new EdgeGatewayAuthCheckResult(
                StatusForbidden,
                "Forward auth request did not come from the local Caddy proxy.");
        }

        var configuration = await configurationStore.LoadAsync(cancellationToken);
        var route = FindRouteForRequest(configuration, requestedHost, requestedPath, enabledOnly: true);
        if (route is null || !route.IsEnabled)
        {
            return new EdgeGatewayAuthCheckResult(
                StatusNotFound,
                "Not Found.");
        }

        if (EdgeGatewayAccessPolicies.IsBlocked(route.AccessPolicy))
        {
            return new EdgeGatewayAuthCheckResult(StatusForbidden, "Route is blocked.");
        }

        if (EdgeGatewayAccessPolicies.IsPassThrough(route.AccessPolicy))
        {
            return Allow(context.User, userEmail, "Pass-through route.");
        }

        if (route.AllowLanOnly && !IsLanAddress(sourceIp))
        {
            return new EdgeGatewayAuthCheckResult(
                StatusForbidden,
                "Route only allows LAN source addresses.");
        }

        // Resolve the effective trusted IP list: route override wins when set, otherwise global.
        var effectiveTrustedIps = route.OverrideGlobalTrustedIps
            ? route.AllowKnownIps
            : configuration.TrustedSourceIps;

        var trustedIpsConfigured = HasConfiguredIpList(effectiveTrustedIps);
        var trustedIpMatched = trustedIpsConfigured && IsIpInList(effectiveTrustedIps, sourceIp);

        // When an override list is active and the IP isn't in it, block immediately.
        if (route.OverrideGlobalTrustedIps && trustedIpsConfigured && !trustedIpMatched)
        {
            return new EdgeGatewayAuthCheckResult(
                StatusForbidden,
                "Source IP did not match the route trusted IP override list.");
        }

        if (route.SkipAuthenticationForKnownIps && trustedIpsConfigured && trustedIpMatched)
        {
            return new EdgeGatewayAuthCheckResult(
                StatusOk,
                "Trusted source IP skip-authentication allowed.",
                UserName: $"known-ip:{sourceIp}");
        }

        string? lanTrustReason = null;
        if (route.LanTrustEnabled && lanClientTrustService is not null)
        {
            var lanTrust = await lanClientTrustService.EvaluateAsync(
                route,
                sourceIp,
                context.ConnectingIp,
                cancellationToken);
            if (lanTrust.IsTrusted)
            {
                return new EdgeGatewayAuthCheckResult(
                    StatusOk,
                    lanTrust.Reason,
                    UserName: string.IsNullOrWhiteSpace(lanTrust.HostName)
                        ? $"lan-trust:{sourceIp}"
                        : $"lan-trust:{lanTrust.HostName}");
            }

            lanTrustReason = lanTrust.Reason;
        }
        else if (route.LanTrustEnabled)
        {
            lanTrustReason = "LAN trust is enabled but the verifier service is not available.";
        }
        else
        {
            lanTrustReason = "Verified LAN trust is disabled for this route.";
        }

        if (EdgeGatewayAccessPolicies.IsTemporaryIpApproval(route.AccessPolicy))
        {
            if (temporaryIpApprovalService is null)
            {
            return DenyTemporaryIpWithDiagnostics(
                accessCheckPageStore,
                route,
                context,
                sourceIp,
                trustedIpsConfigured,
                trustedIpMatched,
                effectiveTrustedIps,
                lanTrustReason,
                "Temporary IP approval service is not available.",
                emailAttempted: false,
                emailSucceeded: false);
            }

            var requestedUrl = BuildRequestedUrl(requestedHost, requestedPath);
            var result = await temporaryIpApprovalService.EvaluateAsync(
                route,
                new TemporaryIpApprovalCheckContext(
                    requestedHost,
                    requestedPath,
                    requestedUrl,
                    sourceIp,
                    context.CountryCode,
                    context.UserAgent),
                cancellationToken);

            if (result.IsAllowed)
            {
                return new EdgeGatewayAuthCheckResult(
                    StatusOk,
                    result.Reason,
                    UserName: $"temporary-ip:{sourceIp}");
            }

            return DenyTemporaryIpWithDiagnostics(
                accessCheckPageStore,
                route,
                context,
                sourceIp,
                trustedIpsConfigured,
                trustedIpMatched,
                effectiveTrustedIps,
                lanTrustReason,
                result.Reason,
                result.EmailAttempted,
                result.EmailSucceeded);
        }

        if (context.User.Identity?.IsAuthenticated != true ||
            !IsMfaOrPasskeySatisfied(context.User))
        {
            return await RedirectToLoginAsync(requestedHost, requestedPath, "MFA/passkey required.", cancellationToken);
        }

        if (!IsUserAllowed(route, userEmail))
        {
            return new EdgeGatewayAuthCheckResult(
                StatusForbidden,
                "Signed-in user is not in the route allow-list.");
        }

        if (!IsGroupAllowed(route, context.User))
        {
            return new EdgeGatewayAuthCheckResult(
                StatusForbidden,
                "Signed-in user is not in an allowed group.");
        }

        return Allow(context.User, userEmail, "Policy allowed.");
    }

    public async Task<string> BuildSafeReturnPathAsync(
        string targetUrl,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSafeReturnTargetAsync(targetUrl, cancellationToken))
        {
            return "/";
        }

        return $"/edge-auth/return?target={Uri.EscapeDataString(targetUrl.Trim())}";
    }

    public async Task<bool> IsSafeReturnTargetAsync(
        string targetUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(targetUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            IsEdgeAuthenticationPath(uri.AbsolutePath))
        {
            return false;
        }

        var configuration = await configurationStore.LoadAsync(cancellationToken);
        var route = FindRouteForRequest(configuration, uri.Host, uri.PathAndQuery, enabledOnly: true);
        return route is { IsEnabled: true };
    }

    private static PublishedApplicationDefinition? FindRouteForRequest(
        EdgeGatewayConfiguration configuration,
        string requestedHost,
        string requestedPath,
        bool enabledOnly)
    {
        var normalizedHost = NormalizeForwardedHost(requestedHost, requestedHost);
        var pathOnly = ExtractPathOnly(requestedPath);

        return configuration.Applications
            .Where(route =>
                NormalizeForwardedHost(route.PublicHostname, route.PublicHostname)
                    .Equals(normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                (!enabledOnly || route.IsEnabled) &&
                RoutePathMatches(route.TargetPathPrefix, pathOnly))
            .OrderByDescending(route => NormalizeRoutePathPrefix(route.TargetPathPrefix).Length)
            .FirstOrDefault();
    }

    private static EdgeGatewayAuthCheckResult DenyTemporaryIpWithDiagnostics(
        IEdgeGatewayAccessCheckPageStore accessCheckPageStore,
        PublishedApplicationDefinition route,
        EdgeGatewayAuthCheckContext context,
        string sourceIp,
        bool trustedIpsConfigured,
        bool trustedIpMatched,
        string effectiveTrustedIps,
        string? lanTrustReason,
        string temporaryIpReason,
        bool emailAttempted,
        bool emailSucceeded)
    {
        if (route.TemporaryIpApprovalUseNotFoundResponse)
        {
            return new EdgeGatewayAuthCheckResult(StatusNotFound, string.Empty, SuppressResponseBody: true);
        }

        var knownSkipEnabled = route.SkipAuthenticationForKnownIps;
        var sourceIpHeader = DescribeSourceIpHeader(context);
        var usingOverride = route.OverrideGlobalTrustedIps;
        var listSource = usingOverride ? "route override list" : "global trusted IPs";
        var knownStatus = !trustedIpsConfigured
            ? "Not configured"
            : trustedIpMatched
                ? knownSkipEnabled
                    ? "Matched, but skip did not apply"
                    : "Matched"
                : "No match";
        var knownDetail = !trustedIpsConfigured
            ? $"No trusted IPs configured ({listSource} is empty). Add your public WAN IP from CF-Connecting-IP ({FormatIpOrMissing(context.ConnectingIp)}) to the {listSource} and enable Skip auth for trusted IPs."
            : trustedIpMatched
                ? knownSkipEnabled
                    ? $"This should have skipped auth. Check Skip auth for trusted IPs is enabled on this route."
                    : $"CF-Connecting-IP / auth IP {sourceIp} matches the {listSource}, but Skip auth for trusted IPs is off."
                : $"Compared auth IP {sourceIp} (from {sourceIpHeader}) against {listSource} ({effectiveTrustedIps}). No match.";

        var lanEnabled = route.LanTrustEnabled;
        var lanOk = false;
        var lanStatus = lanEnabled ? "Not trusted" : "Disabled";
        var lanDetail = lanTrustReason ?? "No LAN trust result.";

        var emailStatus = emailSucceeded
            ? "Email sent"
            : emailAttempted
                ? "Email failed"
                : temporaryIpReason.Contains("pending", StringComparison.OrdinalIgnoreCase)
                    ? "Pending approval"
                    : "Not granted";

        var diagnostics = new EdgeGatewayAccessDiagnostics(
            Title: "Access pending or denied",
            Summary: temporaryIpReason,
            RouteName: route.Name,
            PublicHostname: route.PublicHostname,
            AccessPolicy: route.AccessPolicy,
            SourceIp: string.IsNullOrWhiteSpace(sourceIp) ? "Unknown" : sourceIp,
            SourceIpHeader: sourceIpHeader,
            CloudflareConnectingIp: context.ConnectingIp,
            CountryCode: context.CountryCode,
            UserAgent: TruncateForDiagnostics(context.UserAgent, 160),
            Checks:
            [
                new EdgeGatewayAccessDiagnosticCheck(
                    usingOverride ? "Trusted IPs (route override) vs CF-Connecting-IP" : "Trusted IPs (global) vs CF-Connecting-IP",
                    knownStatus,
                    knownDetail,
                    IsOk: trustedIpsConfigured && trustedIpMatched && knownSkipEnabled),
                new EdgeGatewayAccessDiagnosticCheck(
                    "Verified LAN trust",
                    lanStatus,
                    lanDetail,
                    IsOk: lanOk),
                new EdgeGatewayAccessDiagnosticCheck(
                    "Email approve IP",
                    emailStatus,
                    temporaryIpReason,
                    IsOk: false)
            ],
            NextSteps: BuildTemporaryIpNextSteps(
                trustedIpsConfigured,
                trustedIpMatched,
                knownSkipEnabled,
                usingOverride,
                lanEnabled,
                emailSucceeded,
                temporaryIpReason));

        return new EdgeGatewayAuthCheckResult(
            StatusFound,
            string.Empty,
            RedirectLocation: $"/edge-auth/access-check?token={accessCheckPageStore.Store(diagnostics, TimeSpan.FromMinutes(10))}");
    }

    private static IReadOnlyList<string> BuildTemporaryIpNextSteps(
        bool trustedIpsConfigured,
        bool trustedIpMatched,
        bool knownSkipEnabled,
        bool usingOverride,
        bool lanEnabled,
        bool emailSucceeded,
        string temporaryIpReason)
    {
        var steps = new List<string>();
        var listLabel = usingOverride ? "the route trusted IP override list" : "Security → Global Trusted Source IPs";
        if (!trustedIpsConfigured)
        {
            steps.Add($"For home access through Cloudflare, add your current public WAN IP to {listLabel} and enable Skip auth for trusted IPs on this route.");
        }
        else if (trustedIpMatched && !knownSkipEnabled)
        {
            steps.Add("Enable Skip auth for trusted IPs on this route so matching WAN IPs skip email approval.");
        }
        else if (!trustedIpMatched)
        {
            steps.Add($"Compare CF-Connecting-IP on this page with {listLabel}. Use Get current WAN IP if your public address changed.");
        }

        if (lanEnabled)
        {
            steps.Add("Verified LAN trust only works with split/local DNS that reaches Edge Gateway with a real LAN IP. The public Cloudflare hostname will not satisfy LAN trust.");
        }
        else
        {
            steps.Add("Leave Verified LAN trust off unless you also publish the same hostname via split/local DNS.");
        }

        if (emailSucceeded || temporaryIpReason.Contains("pending", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add("Open the approval email, approve this client, then reload the app.");
        }
        else if (temporaryIpReason.Contains("email", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add("Check Messaging/email settings and that this route has at least one approval recipient.");
        }

        steps.Add("When you no longer need this page, enable Return 404 on the route to hide unapproved clients behind Not Found.");
        return steps;
    }

    private static string TruncateForDiagnostics(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "…";
    }

    private static string DescribeSourceIpHeader(EdgeGatewayAuthCheckContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ConnectingIp))
        {
            return "CF-Connecting-IP";
        }

        if (!string.IsNullOrWhiteSpace(context.ForwardedFor))
        {
            return "X-Forwarded-For";
        }

        return "Direct remote address";
    }

    private static string FormatIpOrMissing(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "not present" : value.Trim();

    private async Task<EdgeGatewayAuthCheckResult> RedirectToLoginAsync(
        string requestedHost,
        string requestedPath,
        string reason,
        CancellationToken cancellationToken)
    {
        var targetPath = IsEdgeAuthenticationPath(requestedPath) ? "/" : requestedPath;
        var targetUrl = BuildRequestedUrl(requestedHost, targetPath);
        var safeReturnPath = await BuildSafeReturnPathAsync(targetUrl, cancellationToken);
        var loginPath = $"/login?returnUrl={Uri.EscapeDataString(safeReturnPath)}&error={Uri.EscapeDataString(reason)}";
        return new EdgeGatewayAuthCheckResult(StatusFound, reason, loginPath);
    }

    private static EdgeGatewayAuthCheckResult Allow(
        ClaimsPrincipal user,
        string userEmail,
        string reason)
    {
        var groups = string.Join(",", ResolveGroups(user));
        return new EdgeGatewayAuthCheckResult(
            StatusOk,
            reason,
            UserName: FindFirstValue(user, ClaimTypes.Name) ?? userEmail,
            UserEmail: userEmail,
            Groups: groups);
    }

    private static bool RoutePathMatches(string? routePathPrefix, string requestedPath)
    {
        var prefix = NormalizeRoutePathPrefix(routePathPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return true;
        }

        var normalizedPath = ExtractPathOnly(requestedPath);
        return normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPathOnly(string? requestedPath)
    {
        var normalized = string.IsNullOrWhiteSpace(requestedPath) ? "/" : requestedPath.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = $"/{normalized}";
        }

        var queryIndex = normalized.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0 ? normalized : normalized[..queryIndex];
    }

    private static bool IsTrustedAuthProxy(IPAddress? remoteIpAddress)
    {
        if (remoteIpAddress is null)
        {
            return false;
        }

        return IPAddress.IsLoopback(remoteIpAddress) ||
               (remoteIpAddress.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIpAddress.MapToIPv4()));
    }

    private static bool IsIpInList(string ipList, string sourceIp)
    {
        var allowed = SplitRouteList(ipList).ToArray();
        if (allowed.Length == 0)
        {
            return false;
        }

        if (!EdgeGatewayIpAddress.TryCanonicalize(sourceIp, out var parsed))
        {
            return false;
        }

        return allowed.Any(item => AddressMatches(parsed, item));
    }

    private static bool HasConfiguredIpList(string ipList) =>
        SplitRouteList(ipList).Any();

    private static bool IsLanAddress(string sourceIp) =>
        EdgeGatewayIpAddress.TryCanonicalize(sourceIp, out var address) &&
        EdgeGatewayIpAddress.IsLanAddress(address);

    private static bool AddressMatches(IPAddress address, string rule)
    {
        var value = (rule ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("private_ranges", StringComparison.OrdinalIgnoreCase))
        {
            return EdgeGatewayIpAddress.IsLanAddress(address);
        }

        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0)
        {
            return IPAddress.TryParse(value, out var exact) && EdgeGatewayIpAddress.AddressesEqual(address, exact);
        }

        if (!IPAddress.TryParse(value[..slashIndex], out var network) ||
            !int.TryParse(value[(slashIndex + 1)..], out var prefixLength))
        {
            return false;
        }

        return AddressInCidr(address, network, prefixLength);
    }

    private static bool AddressInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (network.IsIPv4MappedToIPv6)
        {
            network = network.MapToIPv4();
        }

        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var maxPrefix = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static bool IsUserAllowed(PublishedApplicationDefinition route, string userEmail)
    {
        var allowedUsers = SplitRouteList(route.AllowedUsers).ToArray();
        return allowedUsers.Length == 0 ||
               allowedUsers.Contains(userEmail, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGroupAllowed(PublishedApplicationDefinition route, ClaimsPrincipal user)
    {
        var allowedGroups = SplitRouteList(route.AllowedGroups).ToArray();
        if (allowedGroups.Length == 0)
        {
            return true;
        }

        var groups = ResolveGroups(user);
        return allowedGroups.Intersect(groups, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static IReadOnlyList<string> ResolveGroups(ClaimsPrincipal user) =>
        user.Claims
            .Where(static claim => claim.Type is ClaimTypes.GroupSid or ClaimTypes.Role or "groups" or "lms:group")
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsMfaOrPasskeySatisfied(ClaimsPrincipal user) =>
        user.HasClaim("lms:mfa", "true") ||
        user.HasClaim("lms:passkey", "true") ||
        user.HasClaim("amr", "mfa") ||
        user.HasClaim("amr", "otp") ||
        user.HasClaim("amr", "passkey") ||
        user.HasClaim("amr", "webauthn");

    private static bool IsEdgeAuthenticationPath(string? path)
    {
        var normalized = ExtractPathOnly(path);
        return normalized.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/lmshaauth", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/edge-auth", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/api/passkeys/login", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/api/passkeys/me", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/api/passkeys/register/complete", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForwardedHost(string forwardedHost, string fallbackHost)
    {
        var value = string.IsNullOrWhiteSpace(forwardedHost) ? fallbackHost : forwardedHost;
        var host = value.Split(',')[0].Trim();
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var closingIndex = host.IndexOf(']');
            return closingIndex > 0 ? host[1..closingIndex].ToLowerInvariant() : host.ToLowerInvariant();
        }

        var colonIndex = host.IndexOf(':');
        return (colonIndex > 0 ? host[..colonIndex] : host).Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string NormalizeRoutePathPrefix(string? value)
    {
        var path = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return string.Empty;
        }

        path = "/" + path.Trim('/');
        return path.Replace("//", "/", StringComparison.Ordinal);
    }

    private static string ResolveSourceIp(string connectingIp, string forwardedFor, IPAddress? remoteIpAddress)
    {
        return AuthClientAddress.Resolve(connectingIp, forwardedFor, remoteIpAddress);
    }

    private static IEnumerable<string> SplitRouteList(string? value) =>
        (value ?? string.Empty)
            .Split([',', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildRequestedUrl(string host, string path)
    {
        var normalizedHost = NormalizeForwardedHost(host, host);
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedPath = $"/{normalizedPath}";
        }

        return $"https://{normalizedHost}{normalizedPath}";
    }

    private static string? FindFirstValue(ClaimsPrincipal principal, string claimType) =>
        principal.FindFirst(claimType)?.Value;
}

public sealed record EdgeGatewayAuthCheckContext(
    string ForwardedHost,
    string ForwardedProto,
    string ForwardedUri,
    string ForwardedFor,
    string Host,
    IPAddress? RemoteIpAddress,
    ClaimsPrincipal User,
    string ConnectingIp = "",
    string CountryCode = "",
    string UserAgent = "");

public sealed record EdgeGatewayAuthCheckResult(
    int StatusCode,
    string Reason,
    string? RedirectLocation = null,
    string? UserName = null,
    string? UserEmail = null,
    string? Groups = null,
    bool SuppressResponseBody = false,
    string ContentType = "text/plain; charset=utf-8");
