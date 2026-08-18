using System.Net.Http.Headers;
using System.Text.Json;

namespace LMS.EdgeGateway.Core;

public sealed class CloudflareZoneService(
    HttpClient httpClient,
    ICloudflareApiTokenStore tokenStore) : ICloudflareZoneService
{
    public async Task<CloudflareZoneListResult> ListZonesAsync(CancellationToken cancellationToken = default)
    {
        var token = await tokenStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new CloudflareZoneListResult([], "Cloudflare API token is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "zones?per_page=50&order=name&direction=asc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var success = document.RootElement.TryGetProperty("success", out var successElement) &&
                successElement.ValueKind == JsonValueKind.True;
            if (!response.IsSuccessStatusCode || !success)
            {
                return new CloudflareZoneListResult([], GetError(document.RootElement) ?? "Cloudflare did not return any zones.");
            }

            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                return new CloudflareZoneListResult([], "Cloudflare response did not include a zone list.");
            }

            var zones = result.EnumerateArray()
                .Select(ParseZone)
                .Where(zone => !string.IsNullOrWhiteSpace(zone.Id) && !string.IsNullOrWhiteSpace(zone.Name))
                .ToArray();

            return new CloudflareZoneListResult(zones, null);
        }
        catch
        {
            return new CloudflareZoneListResult([], "Could not load domains from Cloudflare.");
        }
    }

    private static CloudflareZoneSummary ParseZone(JsonElement zone)
    {
        var accountName = string.Empty;
        var accountId = string.Empty;
        if (zone.TryGetProperty("account", out var account))
        {
            if (account.TryGetProperty("name", out var accountNameElement))
            {
                accountName = accountNameElement.GetString() ?? string.Empty;
            }

            if (account.TryGetProperty("id", out var accountIdElement))
            {
                accountId = accountIdElement.GetString() ?? string.Empty;
            }
        }

        var nameServers = zone.TryGetProperty("name_servers", out var nameServersElement) &&
            nameServersElement.ValueKind == JsonValueKind.Array
                ? nameServersElement.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray()
                : [];

        return new CloudflareZoneSummary(
            GetString(zone, "id"),
            GetString(zone, "name"),
            accountId,
            accountName,
            GetString(zone, "status"),
            GetString(zone, "type"),
            nameServers);
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string? GetError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var messages = errors.EnumerateArray()
            .Select(error => error.TryGetProperty("message", out var message) ? message.GetString() : null)
            .Where(message => !string.IsNullOrWhiteSpace(message));

        return string.Join(" ", messages);
    }
}
