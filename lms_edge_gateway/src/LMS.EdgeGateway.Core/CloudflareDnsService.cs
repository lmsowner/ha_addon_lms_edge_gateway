using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class CloudflareDnsService(
    ICloudflareClient client,
    IOptions<EdgeGatewayCoreOptions> options) : ICloudflareDnsService
{
    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
        string apiToken,
        string zoneId,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareDnsRecordDto>(
            apiToken,
            $"zones/{zoneId}/dns_records",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel(zoneId))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudflareDnsRecord> CreateRecordAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord record,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PostAsync<object, CloudflareDnsRecordDto>(
            apiToken,
            $"zones/{zoneId}/dns_records",
            new
            {
                type = record.Type,
                name = record.Name,
                content = record.Content,
                proxied = record.Proxied,
                ttl = record.Ttl,
                comment = string.IsNullOrWhiteSpace(record.Comment)
                    ? options.Value.ManagedRecordComment
                    : record.Comment
            },
            cancellationToken);

        return result.ToModel(zoneId);
    }

    public async Task<CloudflareDnsRecord> UpdateRecordAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord record,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PatchAsync<object, CloudflareDnsRecordDto>(
            apiToken,
            $"zones/{zoneId}/dns_records/{record.Id}",
            new
            {
                type = record.Type,
                name = record.Name,
                content = record.Content,
                proxied = record.Proxied,
                ttl = record.Ttl,
                comment = string.IsNullOrWhiteSpace(record.Comment)
                    ? options.Value.ManagedRecordComment
                    : record.Comment
            },
            cancellationToken);

        return result.ToModel(zoneId);
    }

    public Task DeleteRecordAsync(
        string apiToken,
        string zoneId,
        string recordId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"zones/{zoneId}/dns_records/{recordId}", cancellationToken);
}
