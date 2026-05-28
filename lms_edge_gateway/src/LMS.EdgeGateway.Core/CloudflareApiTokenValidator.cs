using System.Net.Http.Headers;
using System.Text.Json;

namespace LMS.EdgeGateway.Core;

public sealed class CloudflareApiTokenValidator(HttpClient httpClient) : ICloudflareApiTokenValidator
{
    public async Task<CloudflareApiTokenValidationResult> ValidateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new CloudflareApiTokenValidationResult(false, "Enter a Cloudflare API token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "user/tokens/verify");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var isSuccessful = document.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.True;
            var status = document.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : string.Empty;

            if (response.IsSuccessStatusCode && isSuccessful && string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return new CloudflareApiTokenValidationResult(true, "Cloudflare API token is active.");
            }

            return new CloudflareApiTokenValidationResult(false, "Cloudflare rejected this API token.");
        }
        catch
        {
            return new CloudflareApiTokenValidationResult(false, "Could not validate the token with Cloudflare.");
        }
    }
}
