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
        return Normalize(configuration, options.Value);
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

    private static WellKnownConfiguration Normalize(
        WellKnownConfiguration? configuration,
        EdgeGatewayCoreOptions options)
    {
        if (configuration is null)
        {
            return WellKnownConfiguration.Empty;
        }

        return configuration with
        {
            Services = configuration.Services?
                .Where(service => service.Id != Guid.Empty)
                .Select(service => NormalizeService(service, options))
                .ToArray() ?? []
        };
    }

    private static WellKnownService NormalizeService(
        WellKnownService service,
        EdgeGatewayCoreOptions options)
    {
        var domain = service.Domain ?? string.Empty;
        var relativePath = service.RelativePath ?? string.Empty;
        return service with
        {
            DisplayName = service.DisplayName ?? string.Empty,
            Domain = domain,
            RelativePath = relativePath,
            ContentType = service.ContentType ?? string.Empty,
            Body = service.Body ?? string.Empty,
            LastVerificationStatus = service.LastVerificationStatus ?? string.Empty,
            LastVerificationMessage = service.LastVerificationMessage ?? string.Empty,
            CacheControl = string.IsNullOrWhiteSpace(service.CacheControl) ? "no-store" : service.CacheControl,
            Template = service.Template ?? string.Empty,
            PublicUrl = BuildPublicUrlOrExisting(domain, relativePath, service.PublicUrl),
            PublicFilePath = BuildPublicPathOrExisting(options, domain, relativePath, service.PublicFilePath),
            SecretFilePath = service.SecretFilePath ?? string.Empty
        };
    }

    private static string BuildPublicUrlOrExisting(
        string domain,
        string relativePath,
        string existing)
    {
        try
        {
            return WellKnownPath.BuildPublicUrl(domain, relativePath);
        }
        catch
        {
            return existing ?? string.Empty;
        }
    }

    private static string BuildPublicPathOrExisting(
        EdgeGatewayCoreOptions options,
        string domain,
        string relativePath,
        string existing)
    {
        try
        {
            return WellKnownPath.BuildPublicFilePath(
                options.DataRoot,
                options.WellKnownPublicRoot,
                domain,
                relativePath);
        }
        catch
        {
            return existing ?? string.Empty;
        }
    }
}
