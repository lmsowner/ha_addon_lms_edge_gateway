using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonCloudflareApiTokenStore(IOptions<EdgeGatewayCoreOptions> options) : ICloudflareApiTokenStore
{
    private const string TokenKey = "cloudflare_api_token";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<CloudflareApiTokenState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken);
        return new CloudflareApiTokenState(!string.IsNullOrWhiteSpace(token), GetOptionsPath());
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var path = GetOptionsPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty(TokenKey, out var token) &&
                token.ValueKind == JsonValueKind.String
                    ? token.GetString()
                    : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Cloudflare API token cannot be empty.", nameof(token));
        }

        var path = GetOptionsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "/data");

        var root = await ReadExistingOptionsAsync(path, cancellationToken);
        root[TokenKey] = token.Trim();
        root["cloudflare_api_token_validated_at"] = DateTimeOffset.UtcNow;

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
    }

    private static async Task<JsonObject> ReadExistingOptionsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<JsonObject>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private string GetOptionsPath()
    {
        var path = options.Value.OptionsJsonPath;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }
}
