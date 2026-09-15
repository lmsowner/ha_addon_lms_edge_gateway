using System.Text.Json.Serialization;

namespace LMS.EdgeGateway.Core;

public enum DiscoveryExposure
{
    Publishable,
    InternalOnly,
    RequiresManualConfirmation,
    UnsafeToExpose
}

public sealed record LocalHttpServiceEndpoint(
    string Url,
    string Scheme,
    string Host,
    int Port,
    int StatusCode,
    string? Title,
    string? ServerHeader,
    string Scope = "Localhost",
    string? IpAddress = null,
    string? DisplayName = null,
    DateTimeOffset? DiscoveredAtUtc = null,
    int Confidence = 0,
    string ServiceName = "",
    string ServiceKind = "unknown",
    DiscoveryExposure Exposure = DiscoveryExposure.RequiresManualConfirmation,
    string Fingerprint = "",
    IReadOnlyList<string>? Evidence = null,
    string? FaviconDataUrl = null);

public static class LocalHttpServiceDiscoveryRanking
{
    public static int TitleQualityRank(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 2;
        }

        var normalized = title.Trim();
        if (LooksLikeErrorTitle(normalized))
        {
            return 2;
        }

        return 0;
    }

    public static bool LooksLikeErrorTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        var text = title.Trim();
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bad gateway", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bad request", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("internal server", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("service unavailable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
               LooksLikeHttpStatusTitle(text);
    }

    private static bool LooksLikeHttpStatusTitle(string title)
    {
        ReadOnlySpan<char> text = title.AsSpan().Trim();
        if (text.Length is < 3 or > 64)
        {
            return false;
        }

        // Common status-page titles: "404", "404 Not Found", "HTTP 500", "403 - Forbidden"
        for (var i = 0; i <= text.Length - 3; i++)
        {
            if (!char.IsDigit(text[i]) || !char.IsDigit(text[i + 1]) || !char.IsDigit(text[i + 2]))
            {
                continue;
            }

            if (i > 0 && char.IsDigit(text[i - 1]))
            {
                continue;
            }

            if (i + 3 < text.Length && char.IsDigit(text[i + 3]))
            {
                continue;
            }

            var code = (text[i] - '0') * 100 + (text[i + 1] - '0') * 10 + (text[i + 2] - '0');
            if (code is >= 400 and <= 599)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record LocalHttpServiceDiscoveryRequest(
    bool IncludeLocalhost = true,
    bool IncludeLan = true,
    bool IncludeTailnet = false,
    bool IncludeDocker = false);

public sealed record LocalHttpServiceDiscoveryProgressUpdate(
    string Message,
    int ProbedCount,
    int TotalProbeCount,
    int FoundCount,
    LocalHttpServiceEndpoint? FoundEndpoint = null,
    bool IsCompleted = false);

[JsonSerializable(typeof(LocalHttpServiceEndpoint[]))]
internal sealed partial class LocalHttpServiceDiscoveryJsonContext : JsonSerializerContext;
