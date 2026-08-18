using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LMS.EdgeGateway.Core;

public abstract class ApiEmailProviderBase(
    IHttpClientFactory httpClientFactory,
    IEdgeGatewaySecretProtector secretProtector) : IEmailApiProvider
{
    public abstract MessagingEmailProvider Provider { get; }

    public abstract Task<EmailSendResult> SendAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default);

    public async Task<EmailProviderTestResult> TestAsync(
        EdgeGatewayMessagingSettings settings,
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync(settings, message, cancellationToken);
        var summary = result.Success
            ? $"Email accepted by {FormatProvider(Provider)}."
            : result.ErrorMessage;
        return new EmailProviderTestResult(
            result.Success,
            result.Provider,
            result.StatusCode,
            summary,
            result.ProviderMessageId,
            result.RawResponse);
    }

    protected HttpClient CreateClient() => httpClientFactory.CreateClient(nameof(ApiEmailProviderBase));

    protected string ResolveApiKey(EdgeGatewayMessagingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKeyProtected))
        {
            return string.Empty;
        }

        return secretProtector.Unprotect(settings.ApiKeyProtected).Trim();
    }

    protected static string BuildSender(EmailMessage message)
    {
        var fromEmail = message.FromEmail.Trim();
        return string.IsNullOrWhiteSpace(message.FromName)
            ? fromEmail
            : $"{message.FromName.Trim()} <{fromEmail}>";
    }

    protected static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    protected static JsonContent JsonBody(object payload) => JsonContent.Create(payload);

    protected static async Task<string> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Length > 2000 ? body[..2000] : body;
    }

    protected static string ExtractProviderMessageId(string rawResponse, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            foreach (var name in names)
            {
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    protected static string BuildFailureMessage(
        MessagingEmailProvider provider,
        HttpResponseMessage response,
        string rawResponse)
    {
        var failure = ExtractProviderError(rawResponse);
        var hint = BuildCommonHint(rawResponse);
        var detail = string.IsNullOrWhiteSpace(failure)
            ? "The response body was empty."
            : failure;
        return $"{FormatProvider(provider)} send failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}{hint}";
    }

    protected static string FormatProvider(MessagingEmailProvider provider) => provider switch
    {
        MessagingEmailProvider.MicrosoftGraph => "Microsoft Graph",
        MessagingEmailProvider.MailerSend => "MailerSend",
        _ => provider.ToString()
    };

    private static string ExtractProviderError(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;
            foreach (var name in new[] { "message", "error", "error_description", "detail" })
            {
                if (!root.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? rawResponse;
                }

                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("message", out var nestedMessage))
                {
                    return nestedMessage.GetString() ?? rawResponse;
                }
            }
        }
        catch (JsonException)
        {
        }

        return rawResponse;
    }

    private static string BuildCommonHint(string rawResponse)
    {
        if (rawResponse.Contains("domain", StringComparison.OrdinalIgnoreCase) ||
            rawResponse.Contains("sender", StringComparison.OrdinalIgnoreCase) ||
            rawResponse.Contains("verified", StringComparison.OrdinalIgnoreCase))
        {
            return " Check that the sending domain and From address are verified with the provider.";
        }

        return string.Empty;
    }
}
