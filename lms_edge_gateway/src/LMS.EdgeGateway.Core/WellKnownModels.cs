using System.Text.Json;

namespace LMS.EdgeGateway.Core;

public enum WellKnownSourceType
{
    StaticText,
    Json,
    File,
    Generated
}

public enum WellKnownTemplateKind
{
    TeslaFleet,
    SecurityTxt,
    WebFinger,
    AppleAppSiteAssociation,
    AndroidAssetLinks,
    OpenIdConfiguration,
    CustomText,
    CustomJson
}

public sealed record WellKnownService(
    Guid Id,
    string DisplayName,
    string Domain,
    string RelativePath,
    string ContentType,
    string Body,
    WellKnownSourceType SourceType,
    bool Enabled,
    bool RequiresAuth,
    bool PublicReadOnly,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastPublishedUtc = null,
    DateTimeOffset? LastVerifiedUtc = null,
    string LastVerificationStatus = "",
    string LastVerificationMessage = "",
    string CacheControl = "no-store",
    string Template = "",
    string PublicUrl = "",
    string PublicFilePath = "",
    string SecretFilePath = "",
    bool AdvancedContentTypeConfirmed = false,
    bool SensitivePublicBodyConfirmed = false);

public sealed record WellKnownConfiguration(
    IReadOnlyList<WellKnownService> Services,
    DateTimeOffset UpdatedUtc)
{
    public static WellKnownConfiguration Empty { get; } = new([], DateTimeOffset.UtcNow);
}

public sealed record WellKnownTemplateDefinition(
    WellKnownTemplateKind Kind,
    string Name,
    string Description,
    string RelativePath,
    string ContentType,
    WellKnownSourceType SourceType,
    string Body);

public sealed record WellKnownServiceSaveRequest(
    Guid? Id,
    string DisplayName,
    string Domain,
    string RelativePath,
    string ContentType,
    string Body,
    WellKnownSourceType SourceType,
    bool Enabled = true,
    bool RequiresAuth = false,
    bool PublicReadOnly = true,
    string CacheControl = "no-store",
    string Template = "",
    bool AdvancedContentTypeConfirmed = false,
    bool SensitivePublicBodyConfirmed = false);

public sealed record SecurityTxtTemplateRequest(
    string Domain,
    string Contact,
    DateTimeOffset Expires,
    string PreferredLanguages = "en",
    string CanonicalUrl = "",
    string PolicyUrl = "",
    string HiringUrl = "");

public sealed record WellKnownServiceSaveResult(
    bool Success,
    WellKnownService? Service,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

public sealed record WellKnownDeleteResult(
    bool Success,
    Guid ServiceId,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);

public sealed record WellKnownVerificationResult(
    bool Success,
    Guid ServiceId,
    string Status,
    string Message,
    IReadOnlyList<string> Checks,
    DateTimeOffset CheckedUtc);

public static class WellKnownContent
{
    public static string BuildSecurityTxt(SecurityTxtTemplateRequest request)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Contact))
        {
            lines.Add($"Contact: {request.Contact.Trim()}");
        }

        lines.Add($"Expires: {request.Expires.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");

        if (!string.IsNullOrWhiteSpace(request.PreferredLanguages))
        {
            lines.Add($"Preferred-Languages: {request.PreferredLanguages.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.CanonicalUrl))
        {
            lines.Add($"Canonical: {request.CanonicalUrl.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.PolicyUrl))
        {
            lines.Add($"Policy: {request.PolicyUrl.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.HiringUrl))
        {
            lines.Add($"Hiring: {request.HiringUrl.Trim()}");
        }

        return string.Join('\n', lines) + "\n";
    }

    public static string FormatJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
