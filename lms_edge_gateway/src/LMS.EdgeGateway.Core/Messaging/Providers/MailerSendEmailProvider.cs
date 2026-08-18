namespace LMS.EdgeGateway.Core;

public sealed class MailerSendEmailProvider(
    IHttpClientFactory httpClientFactory,
    IEdgeGatewaySecretProtector secretProtector)
    : ApiEmailProviderBase(httpClientFactory, secretProtector)
{
    public override MessagingEmailProvider Provider => MessagingEmailProvider.MailerSend;

    public override async Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var apiToken = ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            return EmailSendResult.Failed(Provider, null, "MailerSend API token is required.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mailersend.com/v1/email");
        request.Headers.Authorization = Bearer(apiToken);
        request.Content = JsonBody(new
        {
            from = new
            {
                email = message.FromEmail.Trim(),
                name = message.FromName.Trim()
            },
            to = new[]
            {
                new
                {
                    email = message.ToEmail.Trim(),
                    name = message.ToName.Trim()
                }
            },
            subject = message.Subject,
            html = message.HtmlBody,
            text = message.PlainTextBody,
            reply_to = string.IsNullOrWhiteSpace(message.ReplyToEmail)
                ? null
                : new { email = message.ReplyToEmail.Trim() }
        });

        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var raw = await ReadResponseBodyAsync(response, cancellationToken);
        var messageId = response.Headers.TryGetValues("X-Message-Id", out var values)
            ? values.FirstOrDefault() ?? string.Empty
            : ExtractProviderMessageId(raw, "message_id", "id");

        return response.IsSuccessStatusCode
            ? EmailSendResult.Succeeded(Provider, (int)response.StatusCode, messageId, raw)
            : EmailSendResult.Failed(Provider, (int)response.StatusCode, BuildFailureMessage(Provider, response, raw), raw);
    }
}
