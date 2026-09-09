using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LMS.EdgeGateway.Core;

public sealed partial class EdgeGatewayEmailDeliveryService(
    IEdgeGatewaySecurityStore securityStore,
    IEdgeGatewaySecretProtector secretProtector,
    IHttpClientFactory httpClientFactory,
    EmailProviderFactory providerFactory,
    IMailRelayStore mailRelayStore,
    IMailRelayHostCommand mailRelayHostCommand,
    ILogger<EdgeGatewayEmailDeliveryService> logger) : IEdgeGatewayEmailDeliveryService
{
    private readonly SemaphoreSlim graphTokenLock = new(1, 1);
    private string? graphAccessToken;
    private DateTimeOffset graphAccessTokenExpiresAtUtc = DateTimeOffset.MinValue;

    public async Task<EmailDeliveryResult> SendHtmlAsync(
        string recipientAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        var settings = (await securityStore.LoadAsync(cancellationToken)).Messaging;
        var message = new EmailMessage(
            settings.SenderAddress,
            settings.SenderDisplayName,
            recipientAddress,
            string.Empty,
            subject,
            StripHtml(htmlBody),
            htmlBody);
        var result = await SendAsync(message, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning(
                "Audit event: MFA email send failed using {Provider}; status {StatusCode}; reason {Reason}.",
                FormatProvider(result.Provider),
                result.StatusCode,
                result.ErrorMessage);
        }

        return new EmailDeliveryResult(
            result.Success,
            result.StatusCode.HasValue,
            result.Success
                ? BuildSuccessMessage(result)
                : result.ErrorMessage);
    }

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var settings = (await securityStore.LoadAsync(cancellationToken)).Messaging;
        return await SendAsync(settings, message, cancellationToken);
    }

    public async Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsEnabled || settings.Provider == MessagingEmailProvider.Disabled)
        {
            return EmailSendResult.Failed(settings.Provider, null, "Email delivery is disabled.");
        }

        var normalized = NormalizeMessage(settings, message);
        if (!MailAddress.TryCreate(normalized.ToEmail, out _))
        {
            return EmailSendResult.Failed(settings.Provider, null, "Enter a valid destination email address.");
        }

        if (!MailAddress.TryCreate(normalized.FromEmail, out _))
        {
            return EmailSendResult.Failed(settings.Provider, null, "Configure a valid sender email address first.");
        }

        return settings.Provider switch
        {
            MessagingEmailProvider.Smtp => await SendSmtpAsync(settings, normalized, cancellationToken),
            MessagingEmailProvider.MailRelay => await SendMailRelayAsync(settings, normalized, cancellationToken),
            MessagingEmailProvider.MicrosoftGraph => await SendGraphAsync(settings, normalized, cancellationToken),
            MessagingEmailProvider.Resend or
            MessagingEmailProvider.Brevo or
            MessagingEmailProvider.MailerSend or
            MessagingEmailProvider.Mailgun => await providerFactory
                .Resolve(settings.Provider)
                .SendAsync(settings, normalized, cancellationToken),
            _ => EmailSendResult.Failed(settings.Provider, null, "Choose an email provider first.")
        };
    }

    private async Task<EmailSendResult> SendMailRelayAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        var domainName = (settings.MailRelaySendingDomain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domainName))
        {
            return EmailSendResult.Failed(MessagingEmailProvider.MailRelay, null, "Choose a Mail Relay sending domain.");
        }

        if (!MailAddress.TryCreate(message.FromEmail, out var sender) ||
            !sender.Host.Equals(domainName, StringComparison.OrdinalIgnoreCase))
        {
            return EmailSendResult.Failed(
                MessagingEmailProvider.MailRelay,
                null,
                $"Sender email must use @{domainName} for this Mail Relay entry.");
        }

        var configuration = await mailRelayStore.GetConfigurationAsync(cancellationToken);
        if (configuration?.Enabled != true)
        {
            return EmailSendResult.Failed(
                MessagingEmailProvider.MailRelay,
                null,
                "Mail Relay is not running. Finish Mail Relay setup first.");
        }

        var domains = await mailRelayStore.ListDomainsAsync(cancellationToken);
        if (!domains.Any(item =>
                item.Enabled &&
                item.DomainName.Equals(domainName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.CurrentDkimPrivateKeySecretReference)))
        {
            return EmailSendResult.Failed(
                MessagingEmailProvider.MailRelay,
                null,
                $"{domainName} is not an enabled Mail Relay sending domain with a DKIM key.");
        }

        if (!MailAddress.TryCreate(message.ToEmail, out var recipient))
        {
            return EmailSendResult.Failed(MessagingEmailProvider.MailRelay, null, "Enter a valid destination email address.");
        }

        var messageId = $"lms-messaging-{Guid.NewGuid():N}@{configuration.RelayHostname}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var submission = await mailRelayHostCommand.RunAsync(
                "sendmail",
                ["-i", "-f", sender.Address, "--", recipient.Address],
                timeout.Token,
                standardInput: Encoding.UTF8.GetBytes(BuildMailRelayMessage(message, sender, recipient, messageId)),
                timeout: TimeSpan.FromSeconds(30));

            if (submission.ExitCode == 127)
            {
                return EmailSendResult.Failed(
                    MessagingEmailProvider.MailRelay,
                    null,
                    "Mail Relay messaging requires the Home Assistant add-on image, where sendmail is installed.");
            }

            if (submission.ExitCode != 0)
            {
                var detail = FirstUsefulLine(submission.StandardError, submission.StandardOutput);
                return EmailSendResult.Failed(
                    MessagingEmailProvider.MailRelay,
                    null,
                    string.IsNullOrWhiteSpace(detail)
                        ? "Mail Relay rejected the local messaging submission."
                        : $"Mail Relay rejected the local messaging submission: {detail}");
            }

            _ = await mailRelayHostCommand.RunAsync(
                "postqueue",
                ["-f"],
                cancellationToken,
                timeout: TimeSpan.FromSeconds(10));
            return EmailSendResult.Succeeded(MessagingEmailProvider.MailRelay, null, messageId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EmailSendResult.Failed(
                MessagingEmailProvider.MailRelay,
                null,
                "Mail Relay did not accept the messaging submission within 30 seconds.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return EmailSendResult.Failed(
                MessagingEmailProvider.MailRelay,
                null,
                $"Mail Relay send failed: {exception.Message}");
        }
    }

    private static string BuildMailRelayMessage(
        EmailMessage message,
        MailAddress sender,
        MailAddress recipient,
        string messageId)
    {
        var fromHeader = string.IsNullOrWhiteSpace(message.FromName)
            ? sender.Address
            : $"{QuoteDisplayName(message.FromName)} <{sender.Address}>";
        var toHeader = string.IsNullOrWhiteSpace(message.ToName)
            ? recipient.Address
            : $"{QuoteDisplayName(message.ToName)} <{recipient.Address}>";
        var useHtml = !string.IsNullOrWhiteSpace(message.HtmlBody);
        var body = useHtml ? message.HtmlBody : message.PlainTextBody;
        var encodedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(body ?? string.Empty));
        var bodyLines = Enumerable.Range(0, (encodedBody.Length + 75) / 76)
            .Select(index => encodedBody.Substring(index * 76, Math.Min(76, encodedBody.Length - (index * 76))));
        var headers = new List<string>
        {
            $"Date: {DateTimeOffset.UtcNow:R}",
            $"Message-ID: <{messageId}>",
            $"From: {fromHeader}",
            $"To: {toHeader}",
            $"Subject: {EncodeHeaderValue(message.Subject)}",
            "MIME-Version: 1.0",
            useHtml
                ? "Content-Type: text/html; charset=utf-8"
                : "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: base64"
        };
        if (!string.IsNullOrWhiteSpace(message.ReplyToEmail) &&
            MailAddress.TryCreate(message.ReplyToEmail, out var replyTo))
        {
            headers.Insert(4, $"Reply-To: {replyTo.Address}");
        }

        return string.Join("\r\n", headers.Concat([string.Empty]).Concat(bodyLines).Concat([string.Empty]));
    }

    private static string QuoteDisplayName(string displayName)
    {
        var trimmed = displayName.Trim();
        return trimmed.Contains('"') || trimmed.Contains('\\') || trimmed.Contains(',') || trimmed.Contains('<')
            ? $"\"{trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : trimmed;
    }

    private static string EncodeHeaderValue(string value)
    {
        var trimmed = (value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
        return Encoding.UTF8.GetByteCount(trimmed) == trimmed.Length
            ? trimmed
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed))}?=";
    }

    private static string FirstUsefulLine(string primary, string fallback) =>
        (primary ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
        ?? (fallback ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
        ?? string.Empty;

    private async Task<EmailSendResult> SendSmtpAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            return EmailSendResult.Failed(MessagingEmailProvider.Smtp, null, "SMTP host is required.");
        }

        using var mailMessage = new MailMessage
        {
            From = new MailAddress(message.FromEmail, message.FromName.Trim()),
            Subject = message.Subject,
            Body = string.IsNullOrWhiteSpace(message.HtmlBody) ? message.PlainTextBody : message.HtmlBody,
            IsBodyHtml = !string.IsNullOrWhiteSpace(message.HtmlBody)
        };
        mailMessage.To.Add(string.IsNullOrWhiteSpace(message.ToName)
            ? new MailAddress(message.ToEmail)
            : new MailAddress(message.ToEmail, message.ToName.Trim()));
        if (!string.IsNullOrWhiteSpace(message.ReplyToEmail))
        {
            mailMessage.ReplyToList.Add(new MailAddress(message.ReplyToEmail.Trim()));
        }

        using var client = new SmtpClient(settings.SmtpHost.Trim(), Math.Clamp(settings.SmtpPort, 1, 65535))
        {
            EnableSsl = settings.SmtpUseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            var password = string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected)
                ? string.Empty
                : secretProtector.Unprotect(settings.SmtpPasswordProtected).Trim();
            client.Credentials = new NetworkCredential(settings.SmtpUsername.Trim(), password);
        }

        try
        {
            await client.SendMailAsync(mailMessage, cancellationToken);
            return EmailSendResult.Succeeded(MessagingEmailProvider.Smtp, null);
        }
        catch (Exception exception)
        {
            return EmailSendResult.Failed(MessagingEmailProvider.Smtp, null, $"SMTP send failed: {exception.Message}");
        }
    }

    private async Task<EmailSendResult> SendGraphAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.GraphTenantId) ||
            string.IsNullOrWhiteSpace(settings.GraphClientId) ||
            string.IsNullOrWhiteSpace(settings.GraphClientSecretProtected))
        {
            return EmailSendResult.Failed(
                MessagingEmailProvider.MicrosoftGraph,
                null,
                "Microsoft Graph tenant id, client id, and client secret are required.");
        }

        var tokenResult = await GetGraphAccessTokenAsync(settings, cancellationToken);
        if (!tokenResult.Succeeded || string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return EmailSendResult.Failed(MessagingEmailProvider.MicrosoftGraph, null, tokenResult.Message);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildGraphSendMailEndpoint(settings.GraphBaseUrl, message.FromEmail));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResult.Token);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                subject = message.Subject,
                body = new
                {
                    contentType = string.IsNullOrWhiteSpace(message.HtmlBody) ? "Text" : "HTML",
                    content = string.IsNullOrWhiteSpace(message.HtmlBody) ? message.PlainTextBody : message.HtmlBody
                },
                toRecipients = new[]
                {
                    new
                    {
                        emailAddress = new
                        {
                            address = message.ToEmail,
                            name = message.ToName
                        }
                    }
                },
                replyTo = string.IsNullOrWhiteSpace(message.ReplyToEmail)
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new
                        {
                            emailAddress = new
                            {
                                address = message.ReplyToEmail.Trim()
                            }
                        }
                    }
            },
            saveToSentItems = settings.GraphSaveToSentItems
        });

        using var client = httpClientFactory.CreateClient(nameof(EdgeGatewayEmailDeliveryService));
        using var response = await client.SendAsync(request, cancellationToken);
        var raw = await ReadFailureMessageAsync(response, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return EmailSendResult.Succeeded(MessagingEmailProvider.MicrosoftGraph, (int)response.StatusCode, rawResponse: raw);
        }

        return EmailSendResult.Failed(
            MessagingEmailProvider.MicrosoftGraph,
            (int)response.StatusCode,
            $"Microsoft Graph sendMail failed with {(int)response.StatusCode} {response.ReasonPhrase}: {raw}",
            raw);
    }

    private async Task<GraphAccessTokenResult> GetGraphAccessTokenAsync(
        EdgeGatewayMessagingSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(graphAccessToken) && graphAccessTokenExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return new GraphAccessTokenResult(true, graphAccessToken, "Using cached Microsoft Graph token.");
        }

        await graphTokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(graphAccessToken) && graphAccessTokenExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return new GraphAccessTokenResult(true, graphAccessToken, "Using cached Microsoft Graph token.");
            }

            var clientSecret = secretProtector.Unprotect(settings.GraphClientSecretProtected).Trim();
            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                return new GraphAccessTokenResult(false, null, "Microsoft Graph client secret could not be resolved. Save it again.");
            }

            using var client = httpClientFactory.CreateClient(nameof(EdgeGatewayEmailDeliveryService));
            using var response = await client.PostAsync(
                BuildGraphTokenEndpoint(settings.GraphAuthority, settings.GraphTenantId),
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.GraphClientId.Trim(),
                    ["client_secret"] = clientSecret,
                    ["scope"] = BuildGraphScope(settings.GraphBaseUrl),
                    ["grant_type"] = "client_credentials"
                }),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var failure = await ReadFailureMessageAsync(response, cancellationToken);
                return new GraphAccessTokenResult(
                    false,
                    null,
                    $"Microsoft Graph token request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {failure}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var tokenElement) ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                return new GraphAccessTokenResult(false, null, "Microsoft Graph token response did not include access_token.");
            }

            var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresInElement) &&
                                   expiresInElement.TryGetInt32(out var parsedExpiresIn)
                ? parsedExpiresIn
                : 3600;

            graphAccessToken = tokenElement.GetString();
            graphAccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds - 60));
            return new GraphAccessTokenResult(true, graphAccessToken, "Microsoft Graph token acquired.");
        }
        finally
        {
            graphTokenLock.Release();
        }
    }

    private static EmailMessage NormalizeMessage(EdgeGatewayMessagingSettings settings, EmailMessage message) =>
        message with
        {
            FromEmail = string.IsNullOrWhiteSpace(message.FromEmail) ? settings.SenderAddress : message.FromEmail.Trim(),
            FromName = string.IsNullOrWhiteSpace(message.FromName) ? settings.SenderDisplayName : message.FromName.Trim(),
            ToEmail = message.ToEmail.Trim(),
            ToName = message.ToName?.Trim() ?? string.Empty,
            Subject = message.Subject.Trim(),
            PlainTextBody = message.PlainTextBody ?? string.Empty,
            HtmlBody = message.HtmlBody ?? string.Empty,
            ReplyToEmail = message.ReplyToEmail?.Trim() ?? string.Empty
        };

    private static string BuildSuccessMessage(EmailSendResult result)
    {
        var provider = FormatProvider(result.Provider);
        var status = result.StatusCode.HasValue ? $" HTTP {result.StatusCode}." : string.Empty;
        var id = string.IsNullOrWhiteSpace(result.ProviderMessageId)
            ? string.Empty
            : $" Provider message id: {result.ProviderMessageId}.";
        return $"Email accepted by {provider}.{status}{id}";
    }

    private static string FormatProvider(MessagingEmailProvider provider) => provider switch
    {
        MessagingEmailProvider.MicrosoftGraph => "Microsoft Graph",
        MessagingEmailProvider.MailerSend => "MailerSend",
        MessagingEmailProvider.MailRelay => "Mail Relay",
        _ => provider.ToString()
    };

    private static string BuildGraphTokenEndpoint(string authorityBaseUrl, string tenantId) =>
        $"{authorityBaseUrl.Trim().TrimEnd('/')}/{tenantId.Trim()}/oauth2/v2.0/token";

    private static string BuildGraphSendMailEndpoint(string graphBaseUrl, string senderAddress) =>
        $"{graphBaseUrl.Trim().TrimEnd('/')}/users/{Uri.EscapeDataString(senderAddress.Trim())}/sendMail";

    private static string BuildGraphScope(string graphBaseUrl)
    {
        var graphBaseUri = new Uri(graphBaseUrl.Trim(), UriKind.Absolute);
        return $"{graphBaseUri.GetLeftPart(UriPartial.Authority)}/.default";
    }

    private static async Task<string> ReadFailureMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return response.IsSuccessStatusCode ? string.Empty : "The response body was empty.";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_description", out var errorDescription))
            {
                return errorDescription.GetString() ?? body;
            }

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString() ?? body;
                }

                if (errorElement.ValueKind == JsonValueKind.Object &&
                    errorElement.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? body;
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 2000 ? body[..2000] : body;
    }

    private static string StripHtml(string value) =>
        HtmlTagRegex().Replace(value ?? string.Empty, string.Empty).Trim();

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    private sealed record GraphAccessTokenResult(bool Succeeded, string? Token, string Message);
}
