namespace LMS.EdgeGateway.Core;

public sealed class BrevoEmailProvider(
    IHttpClientFactory httpClientFactory,
    IEdgeGatewaySecretProtector secretProtector)
    : ApiEmailProviderBase(httpClientFactory, secretProtector)
{
    public override MessagingEmailProvider Provider => MessagingEmailProvider.Brevo;

    public override async Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return EmailSendResult.Failed(Provider, null, "Brevo API key is required.");
        }

        if (IsBrevoSmtpKey(apiKey))
        {
            return EmailSendResult.Failed(
                Provider,
                null,
                "This is a Brevo SMTP key, not a Brevo API key. For the Brevo JSON API provider, create and paste a Brevo API key from Brevo API Keys. Brevo API keys normally start with xkeysib-. To use an xsmtpsib- key, choose SMTP instead.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonBody(new
        {
            sender = new
            {
                name = message.FromName.Trim(),
                email = message.FromEmail.Trim()
            },
            to = new[] { BuildRecipient(message) },
            replyTo = string.IsNullOrWhiteSpace(message.ReplyToEmail)
                ? null
                : new { email = message.ReplyToEmail.Trim() },
            subject = message.Subject,
            htmlContent = message.HtmlBody,
            textContent = message.PlainTextBody
        });

        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var raw = await ReadResponseBodyAsync(response, cancellationToken);
        return response.IsSuccessStatusCode
            ? EmailSendResult.Succeeded(Provider, (int)response.StatusCode, ExtractProviderMessageId(raw, "messageId", "id"), raw)
            : EmailSendResult.Failed(Provider, (int)response.StatusCode, BuildFailureMessage(Provider, response, raw), raw);
    }

    private static bool IsBrevoSmtpKey(string apiKey) =>
        apiKey.Trim().StartsWith("xsmtpsib-", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildRecipient(EmailMessage message)
    {
        var recipient = new Dictionary<string, string>
        {
            ["email"] = message.ToEmail.Trim()
        };

        if (!string.IsNullOrWhiteSpace(message.ToName))
        {
            recipient["name"] = message.ToName.Trim();
        }

        return recipient;
    }
}
