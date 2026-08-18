namespace LMS.EdgeGateway.Core;

public sealed class ResendEmailProvider(
    IHttpClientFactory httpClientFactory,
    IEdgeGatewaySecretProtector secretProtector)
    : ApiEmailProviderBase(httpClientFactory, secretProtector)
{
    public override MessagingEmailProvider Provider => MessagingEmailProvider.Resend;

    public override async Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return EmailSendResult.Failed(Provider, null, "Resend API key is required.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = Bearer(apiKey);
        request.Content = JsonBody(new
        {
            from = BuildSender(message),
            to = new[] { message.ToEmail.Trim() },
            subject = message.Subject,
            html = message.HtmlBody,
            text = message.PlainTextBody,
            reply_to = string.IsNullOrWhiteSpace(message.ReplyToEmail) ? null : message.ReplyToEmail.Trim()
        });

        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var raw = await ReadResponseBodyAsync(response, cancellationToken);
        return response.IsSuccessStatusCode
            ? EmailSendResult.Succeeded(Provider, (int)response.StatusCode, ExtractProviderMessageId(raw, "id"), raw)
            : EmailSendResult.Failed(Provider, (int)response.StatusCode, BuildFailureMessage(Provider, response, raw), raw);
    }
}
