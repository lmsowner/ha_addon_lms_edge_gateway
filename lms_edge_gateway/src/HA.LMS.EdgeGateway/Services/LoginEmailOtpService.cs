using System.Net;
using System.Security.Cryptography;
using System.Text;
using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Caching.Memory;

namespace HA.LMS.EdgeGateway.Services;

public sealed class LoginEmailOtpService(
    IEdgeGatewaySecurityStore securityStore,
    IEdgeGatewayEmailDeliveryService emailDeliveryService,
    IMemoryCache memoryCache,
    ILogger<LoginEmailOtpService> logger)
{
    private const string EmailOtpStatePrefix = "login-email-otp:";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public async Task<LoginEmailOtpSendResult> SendAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return new LoginEmailOtpSendResult(false, "Enter your email address first.");
        }

        var configuration = await securityStore.LoadAsync(cancellationToken);
        var settings = configuration.Messaging;
        if (!CanSendEmailCodes(settings))
        {
            return new LoginEmailOtpSendResult(false, "Email codes are not configured. Use your authenticator code or passkey.");
        }

        var user = configuration.Users.FirstOrDefault(candidate =>
            candidate.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        if (user is null || !user.IsEnabled)
        {
            logger.LogInformation("Login email code requested for unavailable LMS account {Email}.", normalizedEmail);
            return new LoginEmailOtpSendResult(true, "If that LMS account can receive email codes, one has been sent.");
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var state = new LoginEmailOtpState(
            user.Id,
            normalizedEmail,
            salt,
            HashCode(normalizedEmail, code, salt),
            DateTimeOffset.UtcNow.Add(CodeLifetime),
            0);

        memoryCache.Set(BuildCacheKey(normalizedEmail), state, CodeLifetime);

        var message = BuildEmailCodeMessage(settings, user, code);
        var result = await emailDeliveryService.SendAsync(settings, message, cancellationToken);
        if (!result.Success)
        {
            memoryCache.Remove(BuildCacheKey(normalizedEmail));
            logger.LogWarning(
                "Login email code failed for LMS user {UserId}; provider {Provider}; status {StatusCode}; reason {Reason}.",
                user.Id,
                result.Provider,
                result.StatusCode,
                result.ErrorMessage);
            return new LoginEmailOtpSendResult(false, result.ErrorMessage);
        }

        logger.LogInformation("Login email code sent for LMS user {UserId}.", user.Id);
        return new LoginEmailOtpSendResult(true, "Email code sent. Enter it below to sign in.");
    }

    public async Task<SecurityAuthenticationResult> ValidateAsync(
        string email,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || normalizedCode.Length != 6)
        {
            return SecurityAuthenticationResult.Failure("The email code was not valid or has expired.");
        }

        var cacheKey = BuildCacheKey(normalizedEmail);
        if (!memoryCache.TryGetValue<LoginEmailOtpState>(cacheKey, out var state) ||
            state is null ||
            state.ExpiresAtUtc < DateTimeOffset.UtcNow ||
            !state.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            memoryCache.Remove(cacheKey);
            return SecurityAuthenticationResult.Failure("The email code was not valid or has expired.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state.CodeHash),
                Encoding.UTF8.GetBytes(HashCode(normalizedEmail, normalizedCode, state.Salt))))
        {
            if (state.AttemptCount >= 4)
            {
                memoryCache.Remove(cacheKey);
            }
            else
            {
                memoryCache.Set(cacheKey, state with { AttemptCount = state.AttemptCount + 1 }, CodeLifetime);
            }

            return SecurityAuthenticationResult.Failure("The email code was not valid or has expired.");
        }

        memoryCache.Remove(cacheKey);

        var configuration = await securityStore.LoadAsync(cancellationToken);
        var users = configuration.Users.ToList();
        var index = users.FindIndex(candidate => candidate.Id == state.UserId);
        if (index < 0 || !users[index].IsEnabled)
        {
            return SecurityAuthenticationResult.Failure("The LMS account was not found or is disabled.");
        }

        var now = DateTimeOffset.UtcNow;
        users[index] = users[index] with
        {
            LastLoginAtUtc = now,
            UpdatedAtUtc = now
        };

        await securityStore.SaveAsync(configuration with
        {
            Users = users,
            UpdatedAtUtc = now
        }, cancellationToken);

        return SecurityAuthenticationResult.Success(
            users[index].Id,
            users[index].Email,
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(users[index].SessionLifetimeMinutes));
    }

    private static EmailMessage BuildEmailCodeMessage(
        EdgeGatewayMessagingSettings settings,
        EdgeGatewaySecurityUser user,
        string code)
    {
        var encodedCode = WebUtility.HtmlEncode(code);
        var encodedEmail = WebUtility.HtmlEncode(user.Email);
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <body style="margin:0;padding:0;background:#eef5fb;font-family:Inter,Segoe UI,Arial,sans-serif;color:#142033;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#eef5fb;padding:30px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border:1px solid #d9e6f2;border-radius:22px;overflow:hidden;box-shadow:0 16px 40px rgba(15,35,60,.12);">
                      <tr>
                        <td style="padding:28px 30px 8px;">
                          <div style="font-size:12px;letter-spacing:.09em;text-transform:uppercase;color:#169f9a;font-weight:900;">Linux Made Sane - Edge Gateway</div>
                          <h1 style="margin:12px 0 8px;font-size:30px;line-height:1.12;color:#12213a;">Your sign-in code</h1>
                          <p style="margin:0;color:#526174;font-size:15px;line-height:1.55;">Use this code to finish signing in as {{encodedEmail}}.</p>
                        </td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:18px 30px 24px;">
                          <div style="display:inline-block;padding:18px 28px;background:#f4fbff;border:1px solid #cae6f6;border-radius:18px;color:#12213a;font-size:38px;line-height:1;font-weight:900;letter-spacing:.22em;font-family:Consolas,Menlo,monospace;">{{encodedCode}}</div>
                          <p style="margin:18px 0 0;color:#64748b;font-size:13px;line-height:1.5;">This code expires in 10 minutes. Do not forward it.</p>
                        </td>
                      </tr>
                      <tr>
                        <td style="background:#f7fbfe;padding:16px 30px;color:#64748b;font-size:12px;line-height:1.5;">
                          LMS HA Add-On MFA email code
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;

        return new EmailMessage(
            settings.SenderAddress,
            settings.SenderDisplayName,
            user.Email,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName,
            "Your LMS Edge Gateway sign-in code",
            $"Your LMS Edge Gateway sign-in code is {code}. It expires in 10 minutes.",
            html);
    }

    private static bool CanSendEmailCodes(EdgeGatewayMessagingSettings settings) =>
        settings.IsEnabled &&
        settings.Provider != MessagingEmailProvider.Disabled &&
        settings.LastVerifiedAtUtc.HasValue;

    private static string BuildCacheKey(string normalizedEmail) =>
        $"{EmailOtpStatePrefix}{normalizedEmail}";

    private static string NormalizeEmail(string email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    private static string NormalizeCode(string code) =>
        new((code ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string HashCode(string email, string code, string salt)
    {
        var payload = $"{email}:{code}:{salt}:lms-edge-email-otp-v1";
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private sealed record LoginEmailOtpState(
        Guid UserId,
        string Email,
        string Salt,
        string CodeHash,
        DateTimeOffset ExpiresAtUtc,
        int AttemptCount);
}

public sealed record LoginEmailOtpSendResult(bool Succeeded, string Message);
