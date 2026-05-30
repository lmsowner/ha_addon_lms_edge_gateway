using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonCloudflareApiTokenStore(IOptions<EdgeGatewayCoreOptions> options) : ICloudflareApiTokenStore
{
    private const string TokenKey = "cloudflare_api_token";
    private const string TokenValidatedAtKey = "cloudflare_api_token_validated_at";
    private const string TunnelTokenKey = "cloudflare_tunnel_token";
    private const string ApiTokenFileName = "cloudflare-api-token";
    private const string ApiTokenValidatedAtFileName = "cloudflare-api-token.validated-at";
    private const string TunnelTokenFileName = "cloudflared-token";

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
        var token = await GetStringOptionAsync(TokenKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            await WriteSecretFileAsync(GetApiTokenPath(), token, cancellationToken);
            return token;
        }

        return await ReadSecretFileAsync(GetApiTokenPath(), cancellationToken);
    }

    public async Task<string?> GetTunnelTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetStringOptionAsync(TunnelTokenKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            await WriteSecretFileAsync(GetTunnelTokenPath(), token, cancellationToken);
            return token;
        }

        return await ReadSecretFileAsync(GetTunnelTokenPath(), cancellationToken);
    }

    public async Task SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Cloudflare API token cannot be empty.", nameof(token));
        }

        await WriteSecretFileAsync(GetApiTokenPath(), token, cancellationToken);
        await WriteSecretFileAsync(
            GetApiTokenValidatedAtPath(),
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken);
        await SaveStringOptionAsync(TokenKey, token.Trim(), root =>
        {
            root[TokenValidatedAtKey] = DateTimeOffset.UtcNow;
        }, cancellationToken);
    }

    public async Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Cloudflare tunnel token cannot be empty.", nameof(token));
        }

        await WriteSecretFileAsync(GetTunnelTokenPath(), token, cancellationToken);
        await SaveStringOptionAsync(TunnelTokenKey, token.Trim(), null, cancellationToken);
    }

    public async Task ClearTunnelTokenAsync(CancellationToken cancellationToken = default)
    {
        DeleteFileIfExists(GetTunnelTokenPath());

        var path = GetOptionsPath();
        if (!File.Exists(path))
        {
            return;
        }

        var root = await ReadExistingOptionsAsync(path, cancellationToken);
        root.Remove(TunnelTokenKey);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
    }

    public async Task ClearTokenAsync(CancellationToken cancellationToken = default)
    {
        DeleteFileIfExists(GetApiTokenPath());
        DeleteFileIfExists(GetApiTokenValidatedAtPath());
        DeleteFileIfExists(GetTunnelTokenPath());

        var path = GetOptionsPath();
        if (!File.Exists(path))
        {
            return;
        }

        var root = await ReadExistingOptionsAsync(path, cancellationToken);
        root.Remove(TokenKey);
        root.Remove(TokenValidatedAtKey);
        root.Remove(TunnelTokenKey);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
    }

    private async Task<string?> GetStringOptionAsync(string key, CancellationToken cancellationToken)
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
            return document.RootElement.TryGetProperty(key, out var token) &&
                token.ValueKind == JsonValueKind.String
                    ? token.GetString()?.Trim()
                    : null;
        }
        catch
        {
            return null;
        }
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

    private async Task SaveStringOptionAsync(
        string key,
        string value,
        Action<JsonObject>? configureRoot,
        CancellationToken cancellationToken)
    {
        var path = GetOptionsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "/data");

        var root = await ReadExistingOptionsAsync(path, cancellationToken);
        root[key] = value;
        configureRoot?.Invoke(root);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
    }

    private static async Task<string?> ReadSecretFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteSecretFileAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, value.Trim(), cancellationToken);
        TrySetOwnerOnlyFileMode(path);
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup; stale legacy values are still removed from options.json below.
        }
    }

    private static void TrySetOwnerOnlyFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Windows/dev filesystems may not support Unix modes.
        }
    }

    private string GetOptionsPath()
    {
        var path = options.Value.OptionsJsonPath;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }

    private string GetApiTokenPath() =>
        Path.Combine(GetDataRootPath(), ApiTokenFileName);

    private string GetApiTokenValidatedAtPath() =>
        Path.Combine(GetDataRootPath(), ApiTokenValidatedAtFileName);

    private string GetTunnelTokenPath() =>
        Path.Combine(GetDataRootPath(), TunnelTokenFileName);

    private string GetDataRootPath()
    {
        var path = options.Value.DataRoot;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }
}
