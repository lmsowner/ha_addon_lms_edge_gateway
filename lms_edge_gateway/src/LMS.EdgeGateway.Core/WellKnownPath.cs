using System.Text.RegularExpressions;

namespace LMS.EdgeGateway.Core;

public static partial class WellKnownPath
{
    private static readonly HashSet<string> DangerousContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/javascript",
        "text/javascript",
        "image/svg+xml",
        "application/x-sh"
    };

    private static readonly HashSet<string> SafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/jrd+json",
        "application/pkcs7-mime",
        "application/octet-stream",
        "application/x-pem-file",
        "text/plain",
        "text/plain; charset=utf-8"
    };

    public static string NormalizeDomain(string domain)
    {
        var value = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException("Enter a valid domain.");
            }

            value = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Domain is required.");
        }

        if (value.Contains('*', StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            !DomainRegex().IsMatch(value))
        {
            throw new ArgumentException("Enter a valid DNS domain name.");
        }

        return value;
    }

    public static string NormalizeRelativePath(string path)
    {
        var value = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(".well-known path is required.");
        }

        if (value.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Use a relative .well-known path, not an absolute URL.");
        }

        if (value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('\0', StringComparison.Ordinal) ||
            value.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The .well-known path contains invalid characters.");
        }

        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            value = value.StartsWith(".well-known/", StringComparison.OrdinalIgnoreCase)
                ? $"/{value}"
                : $"/.well-known/{value}";
        }

        value = Uri.UnescapeDataString(value);
        while (value.Contains("//", StringComparison.Ordinal))
        {
            value = value.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!value.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The path must be under /.well-known/.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !segments[0].Equals(".well-known", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The path must include a file under /.well-known/.");
        }

        if (segments.Any(segment =>
                segment is "." or ".." ||
                segment.Contains('\0', StringComparison.Ordinal) ||
                segment.Contains(':', StringComparison.Ordinal)))
        {
            throw new ArgumentException("The .well-known path cannot contain traversal segments.");
        }

        return "/" + string.Join('/', segments);
    }

    public static string NormalizeContentType(string contentType, string relativePath, WellKnownSourceType sourceType)
    {
        var value = (contentType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = GuessContentType(relativePath, sourceType);
        }

        if (value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Content-Type cannot contain line breaks.");
        }

        return value;
    }

    public static void ValidateContentType(string contentType, bool advancedContentTypeConfirmed)
    {
        var typeOnly = contentType.Split(';', 2)[0].Trim();
        if (DangerousContentTypes.Contains(typeOnly) && !advancedContentTypeConfirmed)
        {
            throw new ArgumentException($"Content-Type {typeOnly} needs advanced confirmation before publishing.");
        }

        if (!SafeContentTypes.Contains(contentType) &&
            !SafeContentTypes.Contains(typeOnly) &&
            !advancedContentTypeConfirmed)
        {
            throw new ArgumentException($"Content-Type {contentType} is not in the standard safe list. Enable advanced confirmation to publish it.");
        }
    }

    public static void ValidatePublicBody(string body, bool sensitivePublicBodyConfirmed)
    {
        if (ContainsPrivateKeyMaterial(body) && !sensitivePublicBodyConfirmed)
        {
            throw new ArgumentException("The public body appears to contain private key material. Confirm explicitly before publishing it.");
        }
    }

    public static bool ContainsPrivateKeyMaterial(string? body) =>
        !string.IsNullOrWhiteSpace(body) &&
        body.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase) &&
        body.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase);

    public static string BuildPublicUrl(string domain, string relativePath) =>
        $"https://{NormalizeDomain(domain)}{NormalizeRelativePath(relativePath)}";

    public static string BuildPublicFilePath(string dataRoot, string domain, string relativePath) =>
        BuildPublicFilePath(dataRoot, string.Empty, domain, relativePath);

    public static string BuildPublicFilePath(
        string dataRoot,
        string wellKnownPublicRoot,
        string domain,
        string relativePath)
    {
        var root = BuildPublicRoot(dataRoot, wellKnownPublicRoot, domain);
        var segments = NormalizeRelativePath(relativePath)
            .TrimStart('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = Path.Combine([root, .. segments]);
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The .well-known path escapes the public storage root.");
        }

        return fullPath;
    }

    public static string BuildPublicRoot(string dataRoot, string domain) =>
        BuildPublicRoot(dataRoot, string.Empty, domain);

    public static string BuildPublicRoot(string dataRoot, string wellKnownPublicRoot, string domain) =>
        Path.Combine(ResolveWellKnownPublicRoot(dataRoot, wellKnownPublicRoot), NormalizeDomain(domain));

    public static string ResolveWellKnownPublicRoot(string dataRoot, string wellKnownPublicRoot) =>
        string.IsNullOrWhiteSpace(wellKnownPublicRoot)
            ? Path.Combine(ResolveDataRoot(dataRoot), "well-known", "public")
            : ResolveDataRoot(wellKnownPublicRoot);

    public static string GuessContentType(string relativePath, WellKnownSourceType sourceType)
    {
        var path = NormalizeRelativePath(relativePath);
        if (sourceType == WellKnownSourceType.Json || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "application/json";
        }

        if (path.EndsWith("/webfinger", StringComparison.OrdinalIgnoreCase))
        {
            return "application/jrd+json";
        }

        if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
        {
            return "application/x-pem-file";
        }

        return "text/plain; charset=utf-8";
    }

    private static string ResolveDataRoot(string dataRoot) =>
        Path.IsPathRooted(dataRoot) ? dataRoot : Path.GetFullPath(dataRoot);

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainRegex();
}
