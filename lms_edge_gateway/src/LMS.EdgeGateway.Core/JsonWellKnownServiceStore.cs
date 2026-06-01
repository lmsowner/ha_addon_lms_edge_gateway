using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonWellKnownServiceStore(IOptions<EdgeGatewayCoreOptions> options) : IWellKnownServiceStore
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<WellKnownConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        if (!File.Exists(path))
        {
            return WellKnownConfiguration.Empty;
        }

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<WellKnownConfiguration>(stream, jsonOptions, cancellationToken);
        return Normalize(configuration);
    }

    public async Task SaveAsync(
        WellKnownConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.Value.DataRoot);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            configuration with { UpdatedUtc = DateTimeOffset.UtcNow },
            jsonOptions,
            cancellationToken);
    }

    private string GetConfigurationPath() =>
        Path.Combine(ResolveDataRoot(options.Value.DataRoot), "well-known", "services.json");

    private static string ResolveDataRoot(string dataRoot) =>
        Path.IsPathRooted(dataRoot) ? dataRoot : Path.GetFullPath(dataRoot);

    private static WellKnownConfiguration Normalize(WellKnownConfiguration? configuration)
    {
        if (configuration is null)
        {
            return WellKnownConfiguration.Empty;
        }

        return configuration with
        {
            Services = configuration.Services?
                .Where(service => service.Id != Guid.Empty)
                .Select(service => service with
                {
                    DisplayName = service.DisplayName ?? string.Empty,
                    Domain = service.Domain ?? string.Empty,
                    RelativePath = service.RelativePath ?? string.Empty,
                    ContentType = service.ContentType ?? string.Empty,
                    Body = service.Body ?? string.Empty,
                    LastVerificationStatus = service.LastVerificationStatus ?? string.Empty,
                    LastVerificationMessage = service.LastVerificationMessage ?? string.Empty,
                    CacheControl = string.IsNullOrWhiteSpace(service.CacheControl) ? "no-store" : service.CacheControl,
                    Template = service.Template ?? string.Empty,
                    PublicUrl = service.PublicUrl ?? string.Empty,
                    PublicFilePath = service.PublicFilePath ?? string.Empty,
                    SecretFilePath = service.SecretFilePath ?? string.Empty
                })
                .ToArray() ?? []
        };
    }
}
