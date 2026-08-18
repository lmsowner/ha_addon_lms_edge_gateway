using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonEdgeGatewayTemporaryIpApprovalStore(IOptions<EdgeGatewayCoreOptions> options)
    : IEdgeGatewayTemporaryIpApprovalStore
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<TemporaryIpApprovalConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        if (!File.Exists(path))
        {
            return TemporaryIpApprovalConfiguration.Empty;
        }

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<TemporaryIpApprovalConfiguration>(
            stream,
            jsonOptions,
            cancellationToken);
        return Normalize(configuration);
    }

    public async Task SaveAsync(
        TemporaryIpApprovalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.Value.DataRoot);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            Normalize(configuration) with { UpdatedAtUtc = DateTimeOffset.UtcNow },
            jsonOptions,
            cancellationToken);
    }

    private string GetConfigurationPath()
    {
        var dataRoot = options.Value.DataRoot;
        var root = Path.IsPathRooted(dataRoot)
            ? dataRoot
            : Path.GetFullPath(dataRoot);

        return Path.Combine(root, "edge-temporary-ip-approvals.json");
    }

    private static TemporaryIpApprovalConfiguration Normalize(TemporaryIpApprovalConfiguration? configuration)
    {
        if (configuration is null)
        {
            return TemporaryIpApprovalConfiguration.Empty;
        }

        return configuration with
        {
            Requests = configuration.Requests?
                .Where(request => request.Id != Guid.Empty &&
                                  request.RouteId != Guid.Empty &&
                                  !string.IsNullOrWhiteSpace(request.SourceIp))
                .Select(request => request with
                {
                    RouteName = request.RouteName?.Trim() ?? string.Empty,
                    PublicHostname = request.PublicHostname?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty,
                    TargetPathPrefix = request.TargetPathPrefix?.Trim() ?? string.Empty,
                    SourceIp = request.SourceIp?.Trim() ?? string.Empty,
                    CountryCode = NormalizeCountryCode(request.CountryCode),
                    UserAgent = request.UserAgent?.Trim() ?? string.Empty,
                    RequestedUrl = request.RequestedUrl?.Trim() ?? string.Empty,
                    ApprovalTokenHash = request.ApprovalTokenHash ?? string.Empty,
                    LastEmailStatus = request.LastEmailStatus ?? string.Empty
                })
                .ToArray() ?? [],
            Grants = configuration.Grants?
                .Where(grant => grant.Id != Guid.Empty &&
                                grant.RouteId != Guid.Empty &&
                                !string.IsNullOrWhiteSpace(grant.SourceIp))
                .Select(grant => grant with
                {
                    RouteName = grant.RouteName?.Trim() ?? string.Empty,
                    PublicHostname = grant.PublicHostname?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty,
                    TargetPathPrefix = grant.TargetPathPrefix?.Trim() ?? string.Empty,
                    SourceIp = grant.SourceIp?.Trim() ?? string.Empty,
                    CountryCode = NormalizeCountryCode(grant.CountryCode),
                    UserAgent = grant.UserAgent?.Trim() ?? string.Empty
                })
                .ToArray() ?? []
        };
    }

    private static string NormalizeCountryCode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 2 ? normalized : string.Empty;
    }
}
