using System.Text;

namespace LMS.EdgeGateway.Core;

public static class WellKnownCaddyRouteRenderer
{
    public static void AppendRoutes(
        StringBuilder builder,
        IReadOnlyList<WellKnownService> services,
        string dataRoot,
        string wellKnownPublicRoot,
        string forwardAuthUpstream)
    {
        foreach (var service in services
                     .Where(service => service.Enabled)
                     .OrderBy(service => service.Domain, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(service => service.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var domain = WellKnownPath.NormalizeDomain(service.Domain);
            var relativePath = WellKnownPath.NormalizeRelativePath(service.RelativePath);
            var matcherName = $"well_known_{service.Id:N}";
            var contentType = EscapeCaddyValue(service.ContentType);
            var cacheControl = EscapeCaddyValue(
                string.IsNullOrWhiteSpace(service.CacheControl) ? "no-store" : service.CacheControl);

            builder.AppendLine($"        # .well-known service: {SanitizeComment(service.DisplayName)}");
            builder.AppendLine($"        @{matcherName} {{");
            builder.AppendLine($"            host {domain}");
            builder.AppendLine($"            path {relativePath}");
            builder.AppendLine("        }");
            builder.AppendLine($"        handle @{matcherName} {{");
            if (service.RequiresAuth)
            {
                builder.AppendLine($"            forward_auth {forwardAuthUpstream} {{");
                builder.AppendLine("                uri /edge-auth/check");
                builder.AppendLine("                copy_headers X-LMS-User X-LMS-Email X-LMS-Groups");
                builder.AppendLine("                header_up CF-Connecting-IP {http.request.header.CF-Connecting-IP}");
                builder.AppendLine("                header_up CF-IPCountry {http.request.header.CF-IPCountry}");
                builder.AppendLine("            }");
                builder.AppendLine();
            }

            builder.AppendLine($"            header Content-Type \"{contentType}\"");
            builder.AppendLine($"            header Cache-Control \"{cacheControl}\"");
            builder.AppendLine($"            rewrite * /edge-well-known/{service.Id:N}");
            builder.AppendLine($"            reverse_proxy {forwardAuthUpstream} {{");
            builder.AppendLine("                header_up Host {host}");
            builder.AppendLine("                header_up X-Forwarded-Host {host}");
            builder.AppendLine("                header_up X-Forwarded-Proto https");
            builder.AppendLine("                header_up X-LMS-Well-Known-Proxy 1");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
        }
    }

    public static string Render(
        IReadOnlyList<WellKnownService> services,
        string dataRoot,
        string forwardAuthUpstream = "127.0.0.1:5000")
    {
        var builder = new StringBuilder();
        AppendRoutes(builder, services, dataRoot, string.Empty, forwardAuthUpstream);
        return builder.ToString();
    }

    public static string Render(
        IReadOnlyList<WellKnownService> services,
        string dataRoot,
        string wellKnownPublicRoot,
        string forwardAuthUpstream)
    {
        var builder = new StringBuilder();
        AppendRoutes(builder, services, dataRoot, wellKnownPublicRoot, forwardAuthUpstream);
        return builder.ToString();
    }

    private static string EscapeCaddyValue(string value) =>
        (value ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string SanitizeComment(string value) =>
        (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("#", string.Empty, StringComparison.Ordinal)
        .Trim();
}
