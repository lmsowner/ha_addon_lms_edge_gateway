namespace LMS.EdgeGateway.Core;

public interface ICloudflareZoneService
{
    Task<CloudflareZoneListResult> ListZonesAsync(CancellationToken cancellationToken = default);
}

public sealed record CloudflareZoneListResult(
    IReadOnlyList<CloudflareZoneSummary> Zones,
    string? Error);

public sealed record CloudflareZoneSummary(
    string Id,
    string Name,
    string AccountId,
    string AccountName,
    string Status,
    string Type,
    IReadOnlyList<string> NameServers);
