using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonEdgeGatewayConfigurationStore(IOptions<EdgeGatewayCoreOptions> options) : IEdgeGatewayConfigurationStore
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        if (!File.Exists(path))
        {
            return EdgeGatewayConfiguration.Empty;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EdgeGatewayConfiguration>(stream, jsonOptions, cancellationToken)
            ?? EdgeGatewayConfiguration.Empty;
    }

    public async Task SaveAsync(EdgeGatewayConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var path = GetConfigurationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.Value.DataRoot);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, configuration with { UpdatedAtUtc = DateTimeOffset.UtcNow }, jsonOptions, cancellationToken);
    }

    private string GetConfigurationPath()
    {
        var dataRoot = options.Value.DataRoot;
        var root = Path.IsPathRooted(dataRoot)
            ? dataRoot
            : Path.GetFullPath(dataRoot);

        return Path.Combine(root, "edge-gateway.json");
    }
}
