using System.Text;

namespace LMS.EdgeGateway.Core;

public static class PublicProxyRouteCaddyRenderer
{
    public static void AppendRoutes(
        StringBuilder builder,
        IReadOnlyList<PublicProxyRouteDefinition>? routes,
        string forwardAuthUpstream)
    {
        foreach (var route in (routes ?? [])
                     .Where(route => route.Enabled)
                     .OrderBy(route => NormalizeHostname(route.Hostname), StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(route => NormalizePathPrefix(route.PathPrefix).Length)
                     .ThenBy(route => route.Description, StringComparer.OrdinalIgnoreCase))
        {
            var hostname = NormalizeHostname(route.Hostname);
            var pathPrefix = NormalizePathPrefix(route.PathPrefix);
            if (string.IsNullOrWhiteSpace(hostname) ||
                string.IsNullOrWhiteSpace(pathPrefix) ||
                pathPrefix.Equals("/", StringComparison.Ordinal) ||
                !Uri.TryCreate(route.UpstreamUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            var matcherName = $"public_proxy_{route.Id:N}";
            var description = string.IsNullOrWhiteSpace(route.Description)
                ? $"{hostname}{pathPrefix}"
                : route.Description;

            builder.AppendLine($"        # Public proxy route: {SanitizeComment(description)}");
            builder.AppendLine($"        @{matcherName} {{");
            builder.AppendLine($"            host {hostname}");
            builder.AppendLine(route.MatchSubpaths
                ? $"            path {pathPrefix} {pathPrefix}/*"
                : $"            path {pathPrefix}");
            builder.AppendLine("        }");
            builder.AppendLine($"        handle @{matcherName} {{");

            if (route.RequiresAuth)
            {
                builder.AppendLine($"            forward_auth {forwardAuthUpstream} {{");
                builder.AppendLine("                uri /edge-auth/check");
                builder.AppendLine("                copy_headers X-LMS-User X-LMS-Email X-LMS-Groups");
                builder.AppendLine("            }");
                builder.AppendLine();
            }

            builder.AppendLine($"            reverse_proxy {route.UpstreamUrl.Trim()} {{");
            builder.AppendLine(route.PreserveHostHeader
                ? "                header_up Host {host}"
                : "                header_up Host {upstream_hostport}");
            if (route.StripForwardedFor)
            {
                builder.AppendLine("                header_up -X-Forwarded-For");
            }

            builder.AppendLine("                header_up X-Real-IP {remote_host}");
            builder.AppendLine("                header_up X-Forwarded-Host {host}");
            builder.AppendLine("                header_up X-Forwarded-Proto https");
            builder.AppendLine("                header_up X-Forwarded-Port 443");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
        }
    }

    public static string Render(
        IReadOnlyList<PublicProxyRouteDefinition> routes,
        string forwardAuthUpstream = "127.0.0.1:5000")
    {
        var builder = new StringBuilder();
        AppendRoutes(builder, routes, forwardAuthUpstream);
        return builder.ToString();
    }

    private static string NormalizeHostname(string value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizePathPrefix(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return "/";
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = $"/{normalized}";
        }

        return normalized.TrimEnd('/');
    }

    private static string SanitizeComment(string value) =>
        (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("#", string.Empty, StringComparison.Ordinal)
        .Trim();
}
