using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonEdgeGatewaySecurityStore(IOptions<EdgeGatewayCoreOptions> options) : IEdgeGatewaySecurityStore
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<EdgeGatewaySecurityConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        if (!File.Exists(path))
        {
            return EdgeGatewaySecurityConfiguration.Empty;
        }

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<EdgeGatewaySecurityConfiguration>(
            stream,
            jsonOptions,
            cancellationToken);
        return Normalize(configuration);
    }

    public async Task SaveAsync(
        EdgeGatewaySecurityConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.Value.DataRoot);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            Normalize(configuration) with { UpdatedAtUtc = DateTimeOffset.UtcNow },
            jsonOptions,
            cancellationToken);
    }

    private string GetConfigurationPath()
    {
        var dataRoot = options.Value.DataRoot;
        var root = Path.IsPathRooted(dataRoot)
            ? dataRoot
            : Path.GetFullPath(dataRoot);

        return Path.Combine(root, "edge-security.json");
    }

    private static EdgeGatewaySecurityConfiguration Normalize(EdgeGatewaySecurityConfiguration? configuration)
    {
        if (configuration is null)
        {
            return EdgeGatewaySecurityConfiguration.Empty;
        }

        var now = DateTimeOffset.UtcNow;
        return configuration with
        {
            Users = configuration.Users?
                .Where(user => !string.IsNullOrWhiteSpace(user.Email))
                .Select(user => user with
                {
                    Email = user.Email.Trim().ToLowerInvariant(),
                    DisplayName = user.DisplayName?.Trim() ?? string.Empty,
                    OtpSecretProtected = user.OtpSecretProtected ?? string.Empty,
                    SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes)
                })
                .ToArray() ?? [],
            Passkeys = configuration.Passkeys?
                .Where(passkey => passkey.UserId != Guid.Empty && !string.IsNullOrWhiteSpace(passkey.CredentialId))
                .Select(passkey => passkey with
                {
                    CredentialId = passkey.CredentialId ?? string.Empty,
                    PublicKey = passkey.PublicKey ?? string.Empty,
                    UserHandle = passkey.UserHandle ?? string.Empty,
                    FriendlyName = passkey.FriendlyName ?? string.Empty
                })
                .ToArray() ?? [],
            Messaging = NormalizeMessaging(configuration.Messaging, now),
            LoginDesign = NormalizeLoginDesign(configuration.LoginDesign)
        };
    }

    private static EdgeGatewayMessagingSettings NormalizeMessaging(
        EdgeGatewayMessagingSettings? settings,
        DateTimeOffset now)
    {
        var value = settings ?? EdgeGatewayMessagingSettings.CreateDefault(now);
        return value with
        {
            Provider = value.IsEnabled ? value.Provider : MessagingEmailProvider.Disabled,
            SenderAddress = value.SenderAddress?.Trim() ?? string.Empty,
            SenderDisplayName = string.IsNullOrWhiteSpace(value.SenderDisplayName)
                ? "Linux Made Sane"
                : value.SenderDisplayName.Trim(),
            SmtpHost = value.SmtpHost?.Trim() ?? string.Empty,
            SmtpPort = Math.Clamp(value.SmtpPort, 1, 65535),
            SmtpUsername = value.SmtpUsername?.Trim() ?? string.Empty,
            SmtpPasswordProtected = value.SmtpPasswordProtected ?? string.Empty,
            GraphTenantId = value.GraphTenantId?.Trim() ?? string.Empty,
            GraphClientId = value.GraphClientId?.Trim() ?? string.Empty,
            GraphClientSecretProtected = value.GraphClientSecretProtected ?? string.Empty,
            GraphAuthority = string.IsNullOrWhiteSpace(value.GraphAuthority)
                ? "https://login.microsoftonline.com/"
                : value.GraphAuthority.Trim(),
            GraphBaseUrl = string.IsNullOrWhiteSpace(value.GraphBaseUrl)
                ? "https://graph.microsoft.com/v1.0"
                : value.GraphBaseUrl.Trim(),
            ApiKeyProtected = value.ApiKeyProtected ?? string.Empty,
            MailgunDomain = value.MailgunDomain?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty
        };
    }

    private static EdgeGatewayLoginDesignSettings NormalizeLoginDesign(EdgeGatewayLoginDesignSettings? design)
    {
        if (design is null)
        {
            return EdgeGatewayLoginDesignSettings.Default;
        }

        return design with
        {
            Title = string.IsNullOrWhiteSpace(design.Title)
                ? EdgeGatewayLoginDesignSettings.Default.Title
                : design.Title.Trim(),
            Subtitle = string.IsNullOrWhiteSpace(design.Subtitle)
                ? EdgeGatewayLoginDesignSettings.Default.Subtitle
                : design.Subtitle.Trim(),
            Eyebrow = string.IsNullOrWhiteSpace(design.Eyebrow)
                ? EdgeGatewayLoginDesignSettings.Default.Eyebrow
                : design.Eyebrow.Trim(),
            BackgroundImageUrl = string.IsNullOrWhiteSpace(design.BackgroundImageUrl)
                ? EdgeGatewayLoginDesignSettings.Default.BackgroundImageUrl
                : design.BackgroundImageUrl.Trim(),
            AccentColor = string.IsNullOrWhiteSpace(design.AccentColor)
                ? EdgeGatewayLoginDesignSettings.Default.AccentColor
                : design.AccentColor.Trim()
        };
    }
}
