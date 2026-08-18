using System.Net.Http.Headers;
using System.Text;

namespace LMS.EdgeGateway.Core;

public sealed class MailgunEmailProvider(
    IHttpClientFactory httpClientFactory,
    IEdgeGatewaySecretProtector secretProtector)
    : ApiEmailProviderBase(httpClientFactory, secretProtector)
{
    public override MessagingEmailProvider Provider => MessagingEmailProvider.Mailgun;

    public override async Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return EmailSendResult.Failed(Provider, null, "Mailgun API key is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.MailgunDomain))
        {
            return EmailSendResult.Failed(Provider, null, "Mailgun domain is required.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildMailgunEndpoint(settings));
        var credential = Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);

        var values = new Dictionary<string, string>
        {
            ["from"] = BuildSender(message),
            ["to"] = string.IsNullOrWhiteSpace(message.ToName)
                ? message.ToEmail.Trim()
                : $"{message.ToName.Trim()} <{message.ToEmail.Trim()}>",
            ["subject"] = message.Subject,
            ["text"] = message.PlainTextBody,
            ["html"] = message.HtmlBody
        };
        if (!string.IsNullOrWhiteSpace(message.ReplyToEmail))
        {
            values["h:Reply-To"] = message.ReplyToEmail.Trim();
        }

        request.Content = new FormUrlEncodedContent(values);
        using var response = await CreateClient().SendAsync(request, cancellationToken);
        var raw = await ReadResponseBodyAsync(response, cancellationToken);
        return response.IsSuccessStatusCode
            ? EmailSendResult.Succeeded(Provider, (int)response.StatusCode, ExtractProviderMessageId(raw, "id"), raw)
            : EmailSendResult.Failed(Provider, (int)response.StatusCode, BuildFailureMessage(Provider, response, raw), raw);
    }

    private static string BuildMailgunEndpoint(EdgeGatewayMessagingSettings settings)
    {
        var baseUrl = settings.MailgunRegion == MailgunRegion.Eu
            ? "https://api.eu.mailgun.net/v3"
            : "https://api.mailgun.net/v3";
        return $"{baseUrl}/{Uri.EscapeDataString(settings.MailgunDomain.Trim())}/messages";
    }
}
