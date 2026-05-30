using System.Globalization;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewaySecurityService(
    IEdgeGatewaySecurityStore securityStore,
    IEdgeGatewaySecretProtector secretProtector,
    IEdgeGatewayEmailDeliveryService emailDeliveryService,
    ILogger<EdgeGatewaySecurityService> logger) : IEdgeGatewaySecurityService
{
    public async Task<SecuritySettingsPageViewModel> GetPageAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var passkeyCounts = configuration.Passkeys
            .GroupBy(passkey => passkey.UserId)
            .ToDictionary(group => group.Key, group => group.Count());

        return new SecuritySettingsPageViewModel(
            configuration.Users
                .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                .Select(user => MapUser(user, passkeyCounts.TryGetValue(user.Id, out var count) ? count : 0))
                .ToArray(),
            MapMessaging(configuration.Messaging),
            configuration.LoginDesign);
    }

    public async Task<SecurityMessagingSettingsEditor> GetMessagingEditorAsync(CancellationToken cancellationToken = default)
    {
        var settings = (await securityStore.LoadAsync(cancellationToken)).Messaging;
        return new SecurityMessagingSettingsEditor
        {
            IsEnabled = settings.IsEnabled,
            Provider = settings.Provider,
            SenderAddress = settings.SenderAddress,
            SenderDisplayName = settings.SenderDisplayName,
            SmtpHost = settings.SmtpHost,
            SmtpPort = settings.SmtpPort,
            SmtpUseStartTls = settings.SmtpUseStartTls,
            SmtpUsername = settings.SmtpUsername,
            HasSmtpPassword = !string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected),
            GraphTenantId = settings.GraphTenantId,
            GraphClientId = settings.GraphClientId,
            HasGraphClientSecret = !string.IsNullOrWhiteSpace(settings.GraphClientSecretProtected),
            GraphAuthority = settings.GraphAuthority,
            GraphBaseUrl = settings.GraphBaseUrl,
            GraphSaveToSentItems = settings.GraphSaveToSentItems,
            HasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKeyProtected),
            MailgunDomain = settings.MailgunDomain,
            MailgunRegion = settings.MailgunRegion
        };
    }

    public async Task SaveMessagingSettingsAsync(
        SecurityMessagingSettingsEditor editor,
        CancellationToken cancellationToken = default)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var existing = configuration.Messaging;
        var provider = editor.IsEnabled ? editor.Provider : MessagingEmailProvider.Disabled;
        var now = DateTimeOffset.UtcNow;
        var settings = BuildMessagingSettings(existing, editor, now, resetVerification: true);

        ValidateMessagingSettings(settings);
        await securityStore.SaveAsync(configuration with
        {
            Messaging = settings,
            UpdatedAtUtc = now
        }, cancellationToken);

        if (existing.Provider != provider)
        {
            logger.LogInformation(
                "Audit event: Messaging provider changed from {PreviousProvider} to {Provider}.",
                FormatProvider(existing.Provider),
                FormatProvider(provider));
        }

        logger.LogInformation(
            "Audit event: Messaging settings updated for provider {Provider}; enabled {Enabled}.",
            FormatProvider(provider),
            settings.IsEnabled);
    }

    public async Task<SecurityMessagingTestResult> SendMessagingTestAsync(
        SecurityMessagingSettingsEditor editor,
        string recipientAddress,
        CancellationToken cancellationToken = default)
    {
        if (!MailAddress.TryCreate(recipientAddress?.Trim(), out var recipient))
        {
            throw new InvalidOperationException("Enter a valid test recipient email address.");
        }

        var configuration = await securityStore.LoadAsync(cancellationToken);
        var existing = configuration.Messaging;
        var provider = editor.IsEnabled ? editor.Provider : MessagingEmailProvider.Disabled;
        var now = DateTimeOffset.UtcNow;
        var settings = BuildMessagingSettings(existing, editor, now, resetVerification: true);
        ValidateMessagingSettings(settings);
        var secretDiagnostic = BuildMessagingSecretDiagnostic(settings, editor);
        logger.LogInformation(
            "Messaging test prepared for {Provider}; sender {SenderAddress}; secret source {SecretSource}; secret length {SecretLength}.",
            FormatProvider(provider),
            settings.SenderAddress,
            secretDiagnostic.Source,
            secretDiagnostic.Length);

        var result = await emailDeliveryService.SendAsync(
            settings,
            BuildMessagingTestEmailMessage(settings, recipient.Address),
            cancellationToken);

        if (result.Success)
        {
            await securityStore.SaveAsync(configuration with
            {
                Messaging = settings with
                {
                    LastVerifiedAtUtc = now,
                    UpdatedAtUtc = now
                },
                UpdatedAtUtc = now
            }, cancellationToken);

            if (existing.Provider != provider)
            {
                logger.LogInformation(
                    "Audit event: Messaging provider changed from {PreviousProvider} to {Provider}.",
                    FormatProvider(existing.Provider),
                    FormatProvider(provider));
            }

            logger.LogInformation(
                "Audit event: Messaging settings updated for provider {Provider}; enabled {Enabled}.",
                FormatProvider(provider),
                settings.IsEnabled);

            logger.LogInformation(
                "Audit event: Test email sent using {Provider}; status {StatusCode}; provider message id present {HasProviderMessageId}.",
                FormatProvider(result.Provider),
                result.StatusCode,
                !string.IsNullOrWhiteSpace(result.ProviderMessageId));
        }
        else
        {
            logger.LogWarning(
                "Audit event: Test email failed using {Provider}; status {StatusCode}; reason {Reason}.",
                FormatProvider(result.Provider),
                result.StatusCode,
                result.ErrorMessage);
        }

        var message = result.Success
            ? $"Email accepted by {FormatProvider(result.Provider)}."
            : result.ErrorMessage;
        return new SecurityMessagingTestResult(
            result.Success,
            result.StatusCode.HasValue,
            message,
            result.StatusCode,
            FormatProvider(result.Provider),
            result.ProviderMessageId);
    }

    public async Task<SecurityUserProvisioningViewModel> CreateUserAsync(
        SecurityUserEditor editor,
        string? loginUrl = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(editor.Email);
        ValidateEmail(normalizedEmail);

        var configuration = await securityStore.LoadAsync(cancellationToken);
        if (configuration.Users.Any(user => user.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An LMS account with that email already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var secret = TotpAuthenticator.GenerateSecret();
        var user = new EdgeGatewaySecurityUser(
            Guid.NewGuid(),
            normalizedEmail,
            string.IsNullOrWhiteSpace(editor.DisplayName) ? normalizedEmail : editor.DisplayName.Trim(),
            editor.IsEnabled,
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(editor.SessionLifetimeMinutes),
            secretProtector.Protect(secret),
            now,
            now,
            null,
            now);

        await securityStore.SaveAsync(configuration with
        {
            Users = [.. configuration.Users, user],
            UpdatedAtUtc = now
        }, cancellationToken);

        return await BuildProvisioningResultAsync(user, secret, loginUrl, sendEmail: true, cancellationToken);
    }

    public async Task<SecurityUserEditor> GetUserEditorAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetRequiredUserAsync(userId, cancellationToken);
        return new SecurityUserEditor
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            IsEnabled = user.IsEnabled,
            SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes)
        };
    }

    public async Task SaveUserAsync(SecurityUserEditor editor, CancellationToken cancellationToken = default)
    {
        if (!editor.Id.HasValue)
        {
            throw new InvalidOperationException("The LMS account was not found.");
        }

        var normalizedEmail = NormalizeEmail(editor.Email);
        ValidateEmail(normalizedEmail);
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var users = configuration.Users.ToList();
        var index = users.FindIndex(user => user.Id == editor.Id.Value);
        if (index < 0)
        {
            throw new InvalidOperationException("The LMS account was not found.");
        }

        if (users.Any(user => user.Id != editor.Id.Value &&
                              user.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An LMS account with that email already exists.");
        }

        var existing = users[index];
        users[index] = existing with
        {
            Email = normalizedEmail,
            DisplayName = string.IsNullOrWhiteSpace(editor.DisplayName) ? normalizedEmail : editor.DisplayName.Trim(),
            IsEnabled = editor.IsEnabled,
            SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(editor.SessionLifetimeMinutes),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await securityStore.SaveAsync(configuration with
        {
            Users = users,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task SetUserEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var users = configuration.Users.ToList();
        var index = users.FindIndex(user => user.Id == userId);
        if (index < 0)
        {
            throw new InvalidOperationException("The LMS account was not found.");
        }

        users[index] = users[index] with
        {
            IsEnabled = isEnabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await securityStore.SaveAsync(configuration with
        {
            Users = users,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<SecurityUserProvisioningViewModel> ResetUserOtpAsync(
        Guid userId,
        string? loginUrl = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var users = configuration.Users.ToList();
        var index = users.FindIndex(user => user.Id == userId);
        if (index < 0)
        {
            throw new InvalidOperationException("The LMS account was not found.");
        }

        var secret = TotpAuthenticator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var updated = users[index] with
        {
            OtpSecretProtected = secretProtector.Protect(secret),
            OtpResetAtUtc = now,
            UpdatedAtUtc = now
        };
        users[index] = updated;

        await securityStore.SaveAsync(configuration with
        {
            Users = users,
            UpdatedAtUtc = now
        }, cancellationToken);

        return await BuildProvisioningResultAsync(updated, secret, loginUrl, sendEmail: true, cancellationToken);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        await securityStore.SaveAsync(configuration with
        {
            Users = configuration.Users.Where(user => user.Id != userId).ToArray(),
            Passkeys = configuration.Passkeys.Where(passkey => passkey.UserId != userId).ToArray(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<SecurityAuthenticationResult> ValidateOtpAsync(
        string email,
        string otpCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var user = configuration.Users.FirstOrDefault(candidate =>
            candidate.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        if (user is null || !user.IsEnabled)
        {
            return SecurityAuthenticationResult.Failure("The LMS account was not found or is disabled.");
        }

        var secret = secretProtector.Unprotect(user.OtpSecretProtected);
        if (string.IsNullOrWhiteSpace(secret) || !TotpAuthenticator.ValidateCode(secret, otpCode))
        {
            return SecurityAuthenticationResult.Failure("The MFA code was not valid.");
        }

        var users = configuration.Users.ToList();
        var index = users.FindIndex(candidate => candidate.Id == user.Id);
        users[index] = user with
        {
            LastLoginAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await securityStore.SaveAsync(configuration with
        {
            Users = users,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

        return SecurityAuthenticationResult.Success(
            user.Id,
            user.Email,
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes));
    }

    private async Task<EdgeGatewaySecurityUser> GetRequiredUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = (await securityStore.LoadAsync(cancellationToken)).Users.FirstOrDefault(candidate => candidate.Id == userId);
        return user ?? throw new InvalidOperationException("The LMS account was not found.");
    }

    private async Task<SecurityUserProvisioningViewModel> BuildProvisioningResultAsync(
        EdgeGatewaySecurityUser user,
        string secret,
        string? loginUrl,
        bool sendEmail,
        CancellationToken cancellationToken)
    {
        var manualEntryKey = TotpAuthenticator.FormatManualEntryKey(secret);
        var otpUri = TotpAuthenticator.BuildOtpUri(user.Email, secret);
        var emailResult = sendEmail
            ? await SendLoginSetupEmailIfAvailableAsync(user, manualEntryKey, otpUri, loginUrl, cancellationToken)
            : new EmailDeliveryResult(false, false, "Setup QR is ready on this screen.");

        return new SecurityUserProvisioningViewModel(
            user.Id,
            user.Email,
            manualEntryKey,
            otpUri,
            emailResult.Attempted,
            emailResult.Succeeded,
            emailResult.Message);
    }

    private async Task<EmailDeliveryResult> SendLoginSetupEmailIfAvailableAsync(
        EdgeGatewaySecurityUser user,
        string manualEntryKey,
        string otpUri,
        string? loginUrl,
        CancellationToken cancellationToken)
    {
        var settings = (await securityStore.LoadAsync(cancellationToken)).Messaging;
        if (!CanSendLoginSetupEmail(settings))
        {
            return new EmailDeliveryResult(false, false, "Messaging is not enabled and verified.");
        }

        try
        {
            var html = BuildLoginSetupEmailHtml(user, manualEntryKey, otpUri, loginUrl);
            return await emailDeliveryService.SendHtmlAsync(
                user.Email,
                "Your Linux Made Sane Edge Gateway login is ready",
                html,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return new EmailDeliveryResult(false, true, $"Setup email failed: {exception.Message}");
        }
    }

    private static string BuildLoginSetupEmailHtml(
        EdgeGatewaySecurityUser user,
        string manualEntryKey,
        string otpUri,
        string? loginUrl)
    {
        var encodedEmail = WebUtility.HtmlEncode(user.Email);
        var encodedManualEntryKey = WebUtility.HtmlEncode(manualEntryKey);
        var encodedOtpUri = WebUtility.HtmlEncode(otpUri);
        var encodedLoginUrl = WebUtility.HtmlEncode(NormalizeLoginUrl(loginUrl));

        return BuildBrandedEmailHtml(
            "Your login is ready",
            "Authenticator setup",
            "An LMS account has been created for Edge Gateway access. Add the manual key or OTP URI below to your authenticator app, then sign in with your email and MFA code.",
            $$"""
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 20px;">
              <tr>
                <td style="padding:14px 16px;background:#f4f7fb;border:1px solid #dce7f3;border-radius:14px;">
                  <div style="font-size:12px;color:#607089;font-weight:800;text-transform:uppercase;letter-spacing:.08em;">Account</div>
                  <div style="font-size:18px;color:#142033;font-weight:800;margin-top:5px;">{{encodedEmail}}</div>
                </td>
              </tr>
            </table>
            {{BuildEmailQrCodeBlock(otpUri)}}
            {{BuildEmailDetailBlock("Manual key", encodedManualEntryKey, "shield")}}
            {{BuildEmailDetailBlock("OTP URI", encodedOtpUri, "key")}}
            <table role="presentation" cellspacing="0" cellpadding="0" style="margin:22px 0 18px;">
              <tr>
                <td style="background:#0f7b57;border-radius:12px;">
                  <a href="{{encodedLoginUrl}}" style="display:inline-block;color:#ffffff;text-decoration:none;padding:14px 20px;font-weight:900;font-size:15px;">Open Edge Gateway login</a>
                </td>
              </tr>
            </table>
            """,
            "Do not forward this email. If you did not request this account change, contact the person who manages your LMS Edge Gateway add-on.");
    }

    private static EmailMessage BuildMessagingTestEmailMessage(
        EdgeGatewayMessagingSettings settings,
        string recipientAddress)
    {
        const string testCode = "123456";
        var html = BuildBrandedEmailHtml(
            "Messaging is ready",
            "Provider test",
            "This is a test email from Linux Made Sane. If this arrived, Edge Gateway can send MFA, OTP, and security notification emails.",
            $$"""
            {{BuildEmailDetailBlock("Test code", testCode, "key")}}
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:18px 0;">
              <tr>
                <td style="padding:15px 16px;background:#ecfdf5;border:1px solid #bbf7d0;border-radius:14px;color:#14532d;font-size:15px;line-height:1.5;">
                  Messaging is verified for Linux Made Sane - Edge Gateway and ready for the Home Assistant Add-on.
                </td>
              </tr>
            </table>
            """,
            "You received this because someone sent a Messaging test from the LMS Edge Gateway add-on.");

        return new EmailMessage(
            settings.SenderAddress,
            settings.SenderDisplayName,
            recipientAddress,
            string.Empty,
            "LMS test email",
            """
            Linux Made Sane - Edge Gateway
            Home Assistant Add-on

            This is a test email from Linux Made Sane.

            Your test code is: 123456

            Messaging is verified for MFA, OTP, and security notification emails.
            """,
            html);
    }

    private static string BuildBrandedEmailHtml(
        string title,
        string label,
        string intro,
        string bodyHtml,
        string footerNote)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedLabel = WebUtility.HtmlEncode(label);
        var encodedIntro = WebUtility.HtmlEncode(intro);
        var encodedFooterNote = WebUtility.HtmlEncode(footerNote);

        return $$"""
            <!doctype html>
            <html lang="en">
            <body style="margin:0;padding:0;background:#eef3f8;font-family:Inter,Segoe UI,Arial,sans-serif;color:#142033;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#eef3f8;padding:30px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:680px;background:#ffffff;border:1px solid #d9e5f1;border-radius:22px;overflow:hidden;box-shadow:0 16px 40px rgba(15,35,60,.12);">
                      <tr>
                        <td style="background:#07111f;padding:30px 32px;color:#ffffff;">
                          <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
                            <tr>
                              <td width="76" valign="top">
                                {{BuildEmailLogoMark()}}
                              </td>
                              <td valign="top" style="padding-left:12px;">
                                <div style="font-size:13px;letter-spacing:.09em;text-transform:uppercase;color:#94f0c4;font-weight:900;">Linux Made Sane - Edge Gateway</div>
                                <div style="font-size:14px;color:#c7d7e8;margin-top:4px;font-weight:700;">Home Assistant Add-on</div>
                                <h1 style="margin:14px 0 0;font-size:30px;line-height:1.16;font-weight:900;color:#ffffff;">{{encodedTitle}}</h1>
                              </td>
                            </tr>
                          </table>
                        </td>
                      </tr>
                      <tr>
                        <td style="background:#ffffff;padding:0;">
                          {{BuildEmailSplashImage()}}
                          <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
                            <tr>
                              <td style="padding:28px 32px 8px;">
                                <div style="display:inline-block;padding:7px 10px;border-radius:999px;background:#e8f7f0;color:#0f7b57;font-size:12px;font-weight:900;text-transform:uppercase;letter-spacing:.08em;">{{encodedLabel}}</div>
                                <p style="margin:18px 0 22px;font-size:16px;line-height:1.62;color:#334155;">{{encodedIntro}}</p>
                                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 22px;">
                                  <tr>
                                    <td width="33.33%" style="padding:0 7px 0 0;">
                                      {{BuildEmailIconCard("Secure access", "MFA and OTP ready", "shield")}}
                                    </td>
                                    <td width="33.33%" style="padding:0 4px;">
                                      {{BuildEmailIconCard("Home Assistant", "Add-on controlled", "home")}}
                                    </td>
                                    <td width="33.33%" style="padding:0 0 0 7px;">
                                      {{BuildEmailIconCard("Edge Gateway", "Routes protected", "route")}}
                                    </td>
                                  </tr>
                                </table>
                                {{bodyHtml}}
                                <p style="margin:22px 0 0;font-size:13px;line-height:1.55;color:#64748b;">{{encodedFooterNote}}</p>
                              </td>
                            </tr>
                          </table>
                        </td>
                      </tr>
                      <tr>
                        <td style="background:#f6f9fc;padding:18px 32px;color:#64748b;font-size:12px;line-height:1.5;">
                          Linux Made Sane - Edge Gateway &bull; Home Assistant Add-on &bull; Secure local-first access
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

    private static string BuildEmailIconCard(string title, string text, string icon) =>
        $$"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f8fbfd;border:1px solid #e0e9f3;border-radius:14px;">
          <tr>
            <td style="padding:13px 12px;">
              {{BuildEmailIconBadge(icon, 26)}}
              <div style="font-size:13px;font-weight:900;color:#142033;line-height:1.25;">{{WebUtility.HtmlEncode(title)}}</div>
              <div style="font-size:12px;color:#64748b;line-height:1.35;margin-top:3px;">{{WebUtility.HtmlEncode(text)}}</div>
            </td>
          </tr>
        </table>
        """;

    private static string BuildEmailDetailBlock(string label, string value, string icon) =>
        $$"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 16px;">
          <tr>
            <td style="padding:15px 16px;background:#f4f7fb;border:1px solid #dce7f3;border-radius:14px;">
              <table role="presentation" cellspacing="0" cellpadding="0" width="100%">
                <tr>
                  <td width="38" valign="top">{{BuildEmailIconBadge(icon, 28)}}</td>
                  <td valign="top">
                    <div style="font-size:12px;color:#607089;font-weight:900;text-transform:uppercase;letter-spacing:.08em;">{{WebUtility.HtmlEncode(label)}}</div>
                    <div style="margin-top:7px;color:#142033;font-family:Consolas,Menlo,monospace;font-size:14px;line-height:1.45;word-break:break-all;">{{value}}</div>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
        </table>
        """;

    private static string BuildEmailLogoMark() =>
        $$"""
        <table role="presentation" width="64" height="64" cellspacing="0" cellpadding="0" style="width:64px;height:64px;border-collapse:separate;background:#0f7b57;border-radius:16px;border:1px solid #2dd48f;">
          <tr>
            <td align="center" valign="middle" style="padding:6px;">
              <img src="{{LmsLogoImageUrl}}" width="52" height="52" alt="LMS" style="display:block;width:52px;height:52px;border:0;border-radius:12px;color:#ffffff;font-family:Inter,Segoe UI,Arial,sans-serif;font-size:13px;font-weight:900;" />
            </td>
          </tr>
        </table>
        """;

    private static string BuildEmailSplashImage() =>
        $$"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#07111f;">
          <tr>
            <td align="center" style="padding:0 32px 26px;">
              <img src="{{LmsSplashImageUrl}}" width="308" alt="Linux Made Sane splash artwork" style="display:block;width:100%;max-width:308px;height:auto;border:0;border-radius:18px;color:#ffffff;font-family:Inter,Segoe UI,Arial,sans-serif;font-size:15px;font-weight:800;line-height:1.4;" />
            </td>
          </tr>
        </table>
        """;

    private static string BuildEmailQrCodeBlock(string otpUri)
    {
        var qrTable = BuildQrCodeTableHtml(otpUri);
        return $$"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 18px;">
          <tr>
            <td align="center" style="padding:18px 16px;background:#ffffff;border:1px solid #dce7f3;border-radius:16px;">
              <div style="font-size:12px;color:#607089;font-weight:900;text-transform:uppercase;letter-spacing:.08em;margin:0 0 12px;">Scan this QR code</div>
              {{qrTable}}
              <div style="font-size:12px;color:#64748b;line-height:1.45;margin-top:12px;">Open your authenticator app and scan this QR code to add LMS HA Add-On MFA.</div>
            </td>
          </tr>
        </table>
        """;
    }

    private static string BuildQrCodeTableHtml(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload.Trim(), QRCodeGenerator.ECCLevel.Q);
        var builder = new System.Text.StringBuilder();
        const int moduleSize = 3;
        var moduleCount = data.ModuleMatrix.Count;
        var qrSize = moduleCount * moduleSize;
        builder.Append(
            CultureInfo.InvariantCulture,
            $"""<table role="presentation" aria-label="Authenticator QR code" width="{qrSize}" height="{qrSize}" cellspacing="0" cellpadding="0" border="0" style="width:{qrSize}px;height:{qrSize}px;table-layout:fixed;border-collapse:collapse;border-spacing:0;background:#ffffff;border:12px solid #ffffff;margin:0 auto;mso-table-lspace:0pt;mso-table-rspace:0pt;font-size:0;line-height:0;mso-line-height-rule:exactly;">""");

        foreach (var row in data.ModuleMatrix)
        {
            builder.Append(
                CultureInfo.InvariantCulture,
                $"""<tr height="{moduleSize}" style="height:{moduleSize}px;font-size:0;line-height:0;mso-line-height-rule:exactly;padding:0;margin:0;">""");
            foreach (var module in row)
            {
                var color = (bool)module ? "#07111f" : "#ffffff";
                builder.Append(
                    CultureInfo.InvariantCulture,
                    $"""<td width="{moduleSize}" height="{moduleSize}" bgcolor="{color}" style="width:{moduleSize}px;min-width:{moduleSize}px;max-width:{moduleSize}px;height:{moduleSize}px;min-height:{moduleSize}px;max-height:{moduleSize}px;font-size:0;line-height:0;mso-line-height-rule:exactly;background-color:{color};padding:0;margin:0;border:0;overflow:hidden;"></td>""");
            }

            builder.Append("</tr>");
        }

        builder.Append("</table>");
        return builder.ToString();
    }

    private static string BuildEmailIconBadge(string icon, int size)
    {
        var (text, background, color) = icon switch
        {
            "home" => ("HA", "#e8f7f0", "#0f7b57"),
            "key" => ("KEY", "#eaf2ff", "#2563eb"),
            "route" => ("RT", "#fff7ed", "#ea580c"),
            _ => ("MFA", "#e8f7f0", "#0f7b57")
        };
        var fontSize = size <= 26 ? 10 : 11;

        return $$"""
        <table role="presentation" width="{{size}}" height="{{size}}" cellspacing="0" cellpadding="0" style="width:{{size}}px;height:{{size}}px;border-collapse:separate;background:{{background}};border-radius:8px;margin:0 0 9px;">
          <tr>
            <td align="center" valign="middle" style="color:{{color}};font-family:Inter,Segoe UI,Arial,sans-serif;font-size:{{fontSize}}px;line-height:1;font-weight:900;letter-spacing:.02em;">{{text}}</td>
          </tr>
        </table>
        """;
    }

    private const string LmsPublicImageBaseUrl = "https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/src/LinuxMadeSane.Web/wwwroot/images";
    private const string LmsLogoImageUrl = $"{LmsPublicImageBaseUrl}/lms-logo-192.png";
    private const string LmsSplashImageUrl = $"{LmsPublicImageBaseUrl}/lms-splash.png";

    private static string NormalizeLoginUrl(string? loginUrl)
    {
        if (Uri.TryCreate(loginUrl?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return uri.ToString();
        }

        return "http://localhost:5000/login";
    }

    private string ResolveProtectedSecret(
        string existingProtectedSecret,
        string newSecretValue,
        bool keepForActiveProvider)
    {
        if (!keepForActiveProvider)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(newSecretValue)
            ? existingProtectedSecret
            : secretProtector.Protect(newSecretValue.Trim());
    }

    private string ResolveApiProtectedSecret(
        string existingProtectedSecret,
        string newSecretValue,
        MessagingEmailProvider existingProvider,
        MessagingEmailProvider nextProvider)
    {
        if (!IsApiProvider(nextProvider))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(newSecretValue))
        {
            return secretProtector.Protect(newSecretValue.Trim());
        }

        return existingProvider == nextProvider ? existingProtectedSecret : string.Empty;
    }

    private EdgeGatewayMessagingSettings BuildMessagingSettings(
        EdgeGatewayMessagingSettings existing,
        SecurityMessagingSettingsEditor editor,
        DateTimeOffset now,
        bool resetVerification)
    {
        var provider = editor.IsEnabled ? editor.Provider : MessagingEmailProvider.Disabled;
        var smtpPasswordProtected = ResolveProtectedSecret(
            existing.SmtpPasswordProtected,
            editor.SmtpPassword,
            provider == MessagingEmailProvider.Smtp);
        var graphClientSecretProtected = ResolveProtectedSecret(
            existing.GraphClientSecretProtected,
            editor.GraphClientSecret,
            provider == MessagingEmailProvider.MicrosoftGraph);
        var apiKeyProtected = ResolveApiProtectedSecret(
            existing.ApiKeyProtected,
            editor.ApiKey,
            existing.Provider,
            provider);

        return existing with
        {
            IsEnabled = editor.IsEnabled,
            Provider = provider,
            SenderAddress = NormalizeOptional(editor.SenderAddress),
            SenderDisplayName = string.IsNullOrWhiteSpace(editor.SenderDisplayName)
                ? "Linux Made Sane"
                : editor.SenderDisplayName.Trim(),
            SmtpHost = NormalizeOptional(editor.SmtpHost),
            SmtpPort = Math.Clamp(editor.SmtpPort, 1, 65535),
            SmtpUseStartTls = editor.SmtpUseStartTls,
            SmtpUsername = NormalizeOptional(editor.SmtpUsername),
            SmtpPasswordProtected = smtpPasswordProtected,
            GraphTenantId = NormalizeOptional(editor.GraphTenantId),
            GraphClientId = NormalizeOptional(editor.GraphClientId),
            GraphClientSecretProtected = graphClientSecretProtected,
            GraphAuthority = NormalizeAbsoluteUrlOrDefault(editor.GraphAuthority, "https://login.microsoftonline.com/"),
            GraphBaseUrl = NormalizeAbsoluteUrlOrDefault(editor.GraphBaseUrl, "https://graph.microsoft.com/v1.0"),
            GraphSaveToSentItems = editor.GraphSaveToSentItems,
            ApiKeyProtected = apiKeyProtected,
            MailgunDomain = NormalizeDomain(editor.MailgunDomain),
            MailgunRegion = editor.MailgunRegion,
            LastVerifiedAtUtc = resetVerification ? null : existing.LastVerifiedAtUtc,
            UpdatedAtUtc = now
        };
    }

    private static void ValidateMessagingSettings(EdgeGatewayMessagingSettings settings)
    {
        if (!settings.IsEnabled || settings.Provider == MessagingEmailProvider.Disabled)
        {
            return;
        }

        if (!MailAddress.TryCreate(settings.SenderAddress, out _))
        {
            throw new InvalidOperationException("Sender email address is required.");
        }

        if (settings.Provider == MessagingEmailProvider.Smtp)
        {
            if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            {
                throw new InvalidOperationException("SMTP host is required.");
            }

            return;
        }

        if (settings.Provider == MessagingEmailProvider.MicrosoftGraph &&
            (string.IsNullOrWhiteSpace(settings.GraphTenantId) ||
             string.IsNullOrWhiteSpace(settings.GraphClientId) ||
             string.IsNullOrWhiteSpace(settings.GraphClientSecretProtected)))
        {
            throw new InvalidOperationException("Microsoft Graph needs tenant id, client id, and client secret.");
        }

        if (IsApiProvider(settings.Provider) && string.IsNullOrWhiteSpace(settings.ApiKeyProtected))
        {
            throw new InvalidOperationException($"{FormatProvider(settings.Provider)} API key is required.");
        }

        if (settings.Provider == MessagingEmailProvider.Mailgun &&
            string.IsNullOrWhiteSpace(settings.MailgunDomain))
        {
            throw new InvalidOperationException("Mailgun domain is required.");
        }
    }

    private static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeDomain(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private MessagingSecretDiagnostic BuildMessagingSecretDiagnostic(
        EdgeGatewayMessagingSettings settings,
        SecurityMessagingSettingsEditor editor)
    {
        if (!settings.IsEnabled || settings.Provider == MessagingEmailProvider.Disabled)
        {
            return new MessagingSecretDiagnostic("none", 0);
        }

        return settings.Provider switch
        {
            MessagingEmailProvider.Smtp => BuildSecretDiagnostic(editor.SmtpPassword, settings.SmtpPasswordProtected),
            MessagingEmailProvider.MicrosoftGraph => BuildSecretDiagnostic(editor.GraphClientSecret, settings.GraphClientSecretProtected),
            MessagingEmailProvider.Resend or
            MessagingEmailProvider.Brevo or
            MessagingEmailProvider.MailerSend or
            MessagingEmailProvider.Mailgun => BuildSecretDiagnostic(editor.ApiKey, settings.ApiKeyProtected),
            _ => new MessagingSecretDiagnostic("none", 0)
        };
    }

    private MessagingSecretDiagnostic BuildSecretDiagnostic(string enteredSecret, string protectedSecret)
    {
        if (!string.IsNullOrWhiteSpace(enteredSecret))
        {
            return new MessagingSecretDiagnostic("current form", enteredSecret.Trim().Length);
        }

        var resolved = ResolveProtectedSecretForDiagnostics(protectedSecret);
        return string.IsNullOrWhiteSpace(resolved)
            ? new MessagingSecretDiagnostic("none", 0)
            : new MessagingSecretDiagnostic("saved config", resolved.Length);
    }

    private string ResolveProtectedSecretForDiagnostics(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(protectedSecret).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ValidateEmail(string email)
    {
        if (!MailAddress.TryCreate(email, out _))
        {
            throw new InvalidOperationException("Enter a valid email address.");
        }
    }

    private static string NormalizeAbsoluteUrlOrDefault(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Enter a valid absolute URL for {candidate}.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private static SecurityUserViewModel MapUser(EdgeGatewaySecurityUser user, int passkeyCount) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            user.IsEnabled,
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes),
            !string.IsNullOrWhiteSpace(user.OtpSecretProtected),
            passkeyCount,
            user.LastLoginAtUtc,
            user.OtpResetAtUtc);

    private static SecurityMessagingSettingsViewModel MapMessaging(EdgeGatewayMessagingSettings settings) =>
        new(
            settings.IsEnabled,
            settings.Provider,
            settings.SenderAddress,
            settings.SenderDisplayName,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpUseStartTls,
            settings.SmtpUsername,
            !string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected),
            settings.GraphTenantId,
            settings.GraphClientId,
            !string.IsNullOrWhiteSpace(settings.GraphClientSecretProtected),
            settings.GraphAuthority,
            settings.GraphBaseUrl,
            settings.GraphSaveToSentItems,
            !string.IsNullOrWhiteSpace(settings.ApiKeyProtected),
            settings.MailgunDomain,
            settings.MailgunRegion,
            settings.LastVerifiedAtUtc,
            CanSendLoginSetupEmail(settings));

    private static bool CanSendLoginSetupEmail(EdgeGatewayMessagingSettings settings) =>
        settings.IsEnabled &&
        settings.Provider != MessagingEmailProvider.Disabled &&
        settings.LastVerifiedAtUtc.HasValue;

    private static bool IsApiProvider(MessagingEmailProvider provider) =>
        provider is MessagingEmailProvider.Resend
            or MessagingEmailProvider.Brevo
            or MessagingEmailProvider.MailerSend
            or MessagingEmailProvider.Mailgun;

    private static string FormatProvider(MessagingEmailProvider provider) => provider switch
    {
        MessagingEmailProvider.MicrosoftGraph => "Microsoft Graph",
        MessagingEmailProvider.MailerSend => "MailerSend",
        _ => provider.ToString()
    };

    private sealed record MessagingSecretDiagnostic(string Source, int Length);
}
