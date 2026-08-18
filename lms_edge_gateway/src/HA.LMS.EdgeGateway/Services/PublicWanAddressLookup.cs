using System.Net;
using LMS.EdgeGateway.Core;

namespace HA.LMS.EdgeGateway.Services;

public static class PublicWanAddressLookup
{
    private static readonly string[] LookupUrls =
    [
        "https://api.ipify.org",
        "https://api6.ipify.org"
    ];

    public static async Task<IReadOnlyList<string>> LookupAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        var found = new List<string>();
        foreach (var url in LookupUrls)
        {
            try
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var value = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (!IPAddress.TryParse(value, out var parsed))
                {
                    continue;
                }

                var canonical = EdgeGatewayIpAddress.Canonicalize(parsed).ToString();
                if (!found.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(canonical);
                }
            }
            catch (Exception) when (found.Count > 0)
            {
                // Keep any address already resolved from the other family.
            }
        }

        if (found.Count == 0)
        {
            throw new InvalidOperationException("Lookup did not return a valid IPv4 or IPv6 address.");
        }

        return found;
    }
}
