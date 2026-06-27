namespace LMS.EdgeGateway.Core;

public enum MessagingEmailProvider
{
    Disabled = 0,
    Smtp = 1,
    MicrosoftGraph = 2,
    Resend = 3,
    Brevo = 4,
    MailerSend = 5,
    Mailgun = 6
}

public enum MailgunRegion
{
    Us = 0,
    Eu = 1
}

public static class SecuritySessionPolicy
{
    public const int MinimumSessionLifetimeMinutes = 5;
    public const int DefaultSessionLifetimeMinutes = 60;
    public const int MaximumSessionLifetimeMinutes = 43_200;

    public static int NormalizeSessionLifetimeMinutes(int minutes) =>
        minutes <= 0
            ? DefaultSessionLifetimeMinutes
            : Math.Clamp(minutes, MinimumSessionLifetimeMinutes, MaximumSessionLifetimeMinutes);
}

public sealed record EdgeGatewaySecurityUser(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsEnabled,
    int SessionLifetimeMinutes,
    string OtpSecretProtected,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? OtpResetAtUtc);

public sealed record EdgeGatewayPasskeyCredential(
    Guid Id,
    Guid UserId,
    string CredentialId,
    string PublicKey,
    string UserHandle,
    uint SignatureCounter,
    string FriendlyName,
    bool IsBackedUp,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastUsedAtUtc);

public sealed record EdgeGatewayMessagingSettings(
    bool IsEnabled,
    MessagingEmailProvider Provider,
    string SenderAddress,
    string SenderDisplayName,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseStartTls,
    string SmtpUsername,
    string SmtpPasswordProtected,
    string GraphTenantId,
    string GraphClientId,
    string GraphClientSecretProtected,
    string GraphAuthority,
    string GraphBaseUrl,
    bool GraphSaveToSentItems,
    string ApiKeyProtected,
    string MailgunDomain,
    MailgunRegion MailgunRegion,
    DateTimeOffset? LastVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static EdgeGatewayMessagingSettings CreateDefault(DateTimeOffset now) =>
        new(
            false,
            MessagingEmailProvider.Disabled,
            string.Empty,
            "Linux Made Sane",
            string.Empty,
            587,
            true,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "https://login.microsoftonline.com/",
            "https://graph.microsoft.com/v1.0",
            true,
            string.Empty,
            string.Empty,
            MailgunRegion.Us,
            null,
            now,
            now);
}

public sealed record EdgeGatewayLoginDesignSettings(
    string Title,
    string Subtitle,
    string Eyebrow,
    string BackgroundImageUrl,
    string AccentColor)
{
    public static EdgeGatewayLoginDesignSettings Default { get; } = new(
        "Verify access.",
        "Email and MFA. No LMS password.",
        "Linux Made Sane",
        "images/lms-auth-panel.png",
        "#007aff");
}

public sealed record EdgeGatewaySecurityConfiguration(
    IReadOnlyList<EdgeGatewaySecurityUser> Users,
    IReadOnlyList<EdgeGatewayPasskeyCredential> Passkeys,
    EdgeGatewayMessagingSettings Messaging,
    EdgeGatewayLoginDesignSettings LoginDesign,
    DateTimeOffset UpdatedAtUtc)
{
    public static EdgeGatewaySecurityConfiguration Empty
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return new EdgeGatewaySecurityConfiguration(
                [],
                [],
                EdgeGatewayMessagingSettings.CreateDefault(now),
                EdgeGatewayLoginDesignSettings.Default,
                now);
        }
    }
}

public sealed record SecurityUserViewModel(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsEnabled,
    int SessionLifetimeMinutes,
    bool HasOtpSecret,
    int PasskeyCount,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? OtpResetAtUtc);

public sealed record SecurityMessagingSettingsViewModel(
    bool IsEnabled,
    MessagingEmailProvider Provider,
    string SenderAddress,
    string SenderDisplayName,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseStartTls,
    string SmtpUsername,
    bool HasSmtpPassword,
    string GraphTenantId,
    string GraphClientId,
    bool HasGraphClientSecret,
    string GraphAuthority,
    string GraphBaseUrl,
    bool GraphSaveToSentItems,
    bool HasApiKey,
    string MailgunDomain,
    MailgunRegion MailgunRegion,
    DateTimeOffset? LastVerifiedAtUtc,
    bool CanSendLoginSetupEmail);

public sealed record SecuritySettingsPageViewModel(
    IReadOnlyList<SecurityUserViewModel> Users,
    SecurityMessagingSettingsViewModel Messaging,
    EdgeGatewayLoginDesignSettings LoginDesign,
    IReadOnlyList<TrustedIpAddressViewModel> TrustedIpAddresses);

public sealed class SecurityUserEditor
{
    public Guid? Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int SessionLifetimeMinutes { get; set; } = SecuritySessionPolicy.DefaultSessionLifetimeMinutes;
}

public sealed class SecurityMessagingSettingsEditor
{
    public bool IsEnabled { get; set; }
    public MessagingEmailProvider Provider { get; set; } = MessagingEmailProvider.Disabled;
    public string SenderAddress { get; set; } = string.Empty;
    public string SenderDisplayName { get; set; } = "Linux Made Sane";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public bool HasSmtpPassword { get; set; }
    public string GraphTenantId { get; set; } = string.Empty;
    public string GraphClientId { get; set; } = string.Empty;
    public string GraphClientSecret { get; set; } = string.Empty;
    public bool HasGraphClientSecret { get; set; }
    public string GraphAuthority { get; set; } = "https://login.microsoftonline.com/";
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public bool GraphSaveToSentItems { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public string MailgunDomain { get; set; } = string.Empty;
    public MailgunRegion MailgunRegion { get; set; } = MailgunRegion.Us;
}

public sealed record SecurityUserProvisioningViewModel(
    Guid UserId,
    string Email,
    string ManualEntryKey,
    string OtpUri,
    bool EmailAttempted,
    bool EmailSucceeded,
    string EmailMessage);

public sealed record SecurityMessagingTestResult(
    bool Succeeded,
    bool Attempted,
    string Message,
    int? StatusCode = null,
    string Provider = "",
    string ProviderMessageId = "");

public sealed record SecurityAuthenticationResult(
    bool Succeeded,
    Guid? UserId,
    string Email,
    int SessionLifetimeMinutes,
    string Message)
{
    public static SecurityAuthenticationResult Success(Guid userId, string email, int sessionLifetimeMinutes) =>
        new(true, userId, email, sessionLifetimeMinutes, string.Empty);

    public static SecurityAuthenticationResult Failure(string message) =>
        new(false, null, string.Empty, SecuritySessionPolicy.DefaultSessionLifetimeMinutes, message);
}

public sealed record EmailDeliveryResult(
    bool Succeeded,
    bool Attempted,
    string Message);

public sealed record EmailMessage(
    string FromEmail,
    string FromName,
    string ToEmail,
    string ToName,
    string Subject,
    string PlainTextBody,
    string HtmlBody,
    string ReplyToEmail = "");

public sealed record EmailSendResult(
    bool Success,
    MessagingEmailProvider Provider,
    string ProviderMessageId,
    int? StatusCode,
    string ErrorMessage,
    string RawResponse = "")
{
    public static EmailSendResult Succeeded(
        MessagingEmailProvider provider,
        int? statusCode,
        string providerMessageId = "",
        string rawResponse = "") =>
        new(true, provider, providerMessageId, statusCode, string.Empty, rawResponse);

    public static EmailSendResult Failed(
        MessagingEmailProvider provider,
        int? statusCode,
        string errorMessage,
        string rawResponse = "") =>
        new(false, provider, string.Empty, statusCode, errorMessage, rawResponse);
}

public sealed record EmailProviderTestResult(
    bool Success,
    MessagingEmailProvider Provider,
    int? StatusCode,
    string Message,
    string ProviderMessageId = "",
    string RawResponse = "");

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public interface IEmailApiProvider
{
    MessagingEmailProvider Provider { get; }

    Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default);

    Task<EmailProviderTestResult> TestAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default);
}
