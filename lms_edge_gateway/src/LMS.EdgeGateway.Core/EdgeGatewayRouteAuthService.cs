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
    IEdgeGatewayTemporaryIpApprovalService? temporaryIpApprovalService = null) : IEdgeGatewayRouteAuthService
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

        var route = await FindRouteForRequestAsync(requestedHost, requestedPath, enabledOnly: true, cancellationToken);
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

        if (!IsKnownIpAllowed(route, sourceIp))
        {
            return new EdgeGatewayAuthCheckResult(
                StatusForbidden,
                "Source IP did not match the route allow-list.");
        }

        if (EdgeGatewayAccessPolicies.IsTemporaryIpApproval(route.AccessPolicy))
        {
            if (temporaryIpApprovalService is null)
            {
                return new EdgeGatewayAuthCheckResult(
                    StatusForbidden,
                    "Temporary IP approval service is not available.");
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

            return route.TemporaryIpApprovalUseNotFoundResponse
                ? new EdgeGatewayAuthCheckResult(StatusNotFound, string.Empty, SuppressResponseBody: true)
                : new EdgeGatewayAuthCheckResult(StatusForbidden, result.Reason);
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

        var route = await FindRouteForRequestAsync(uri.Host, uri.PathAndQuery, enabledOnly: true, cancellationToken);
        return route is { IsEnabled: true };
    }

    private async Task<PublishedApplicationDefinition?> FindRouteForRequestAsync(
        string requestedHost,
        string requestedPath,
        bool enabledOnly,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeForwardedHost(requestedHost, requestedHost);
        var pathOnly = ExtractPathOnly(requestedPath);
        var configuration = await configurationStore.LoadAsync(cancellationToken);

        return configuration.Applications
            .Where(route =>
                NormalizeForwardedHost(route.PublicHostname, route.PublicHostname)
                    .Equals(normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                (!enabledOnly || route.IsEnabled) &&
                RoutePathMatches(route.TargetPathPrefix, pathOnly))
            .OrderByDescending(route => NormalizeRoutePathPrefix(route.TargetPathPrefix).Length)
            .FirstOrDefault();
    }

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
               remoteIpAddress.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIpAddress.MapToIPv4());
    }

    private static bool IsKnownIpAllowed(PublishedApplicationDefinition route, string sourceIp)
    {
        var allowed = SplitRouteList(route.AllowKnownIps).ToArray();
        if (allowed.Length == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(sourceIp, out var parsed))
        {
            return false;
        }

        return allowed.Any(item => AddressMatches(parsed, item));
    }

    private static bool IsLanAddress(string sourceIp)
    {
        if (!IPAddress.TryParse(sourceIp, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private static bool AddressMatches(IPAddress address, string rule)
    {
        var value = (rule ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("private_ranges", StringComparison.OrdinalIgnoreCase))
        {
            return IsLanAddress(address.ToString());
        }

        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0)
        {
            return IPAddress.TryParse(value, out var exact) && AddressesEqual(address, exact);
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

    private static bool AddressesEqual(IPAddress left, IPAddress right)
    {
        if (left.IsIPv4MappedToIPv6)
        {
            left = left.MapToIPv4();
        }

        if (right.IsIPv4MappedToIPv6)
        {
            right = right.MapToIPv4();
        }

        return left.Equals(right);
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
        var cloudflareConnectingIp = (connectingIp ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(cloudflareConnectingIp))
        {
            return cloudflareConnectingIp;
        }

        var firstForwarded = (forwardedFor ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstForwarded)
            ? remoteIpAddress?.ToString() ?? string.Empty
            : firstForwarded;
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
    bool SuppressResponseBody = false);
