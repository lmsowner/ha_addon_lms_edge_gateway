using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class JsonCloudflareApiTokenStoreTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"lms-edge-token-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task Saved_tunnel_token_survives_without_options_json()
    {
        var store = CreateStore();

        await store.SaveTunnelTokenAsync(" tunnel-token ");
        File.Delete(OptionsPath);

        var token = await store.GetTunnelTokenAsync();

        Assert.Equal("tunnel-token", token);
        Assert.Equal("tunnel-token", await File.ReadAllTextAsync(TunnelTokenPath));
    }

    [Fact]
    public async Task Legacy_options_tunnel_token_is_migrated_to_persistent_file()
    {
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(OptionsPath, """{"cloudflare_tunnel_token":" legacy-token "}""");
        var store = CreateStore();

        var token = await store.GetTunnelTokenAsync();

        Assert.Equal("legacy-token", token);
        Assert.Equal("legacy-token", await File.ReadAllTextAsync(TunnelTokenPath));
    }

    [Fact]
    public async Task Options_tunnel_token_wins_over_stale_persistent_file()
    {
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(OptionsPath, """{"cloudflare_tunnel_token":" current-token "}""");
        await File.WriteAllTextAsync(TunnelTokenPath, "stale-token");
        var store = CreateStore();

        var token = await store.GetTunnelTokenAsync();

        Assert.Equal("current-token", token);
        Assert.Equal("current-token", await File.ReadAllTextAsync(TunnelTokenPath));
    }

    [Fact]
    public async Task Options_api_token_wins_over_stale_persistent_file()
    {
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(OptionsPath, """{"cloudflare_api_token":" current-api "}""");
        await File.WriteAllTextAsync(ApiTokenPath, "stale-api");
        var store = CreateStore();

        var token = await store.GetTokenAsync();

        Assert.Equal("current-api", token);
        Assert.Equal("current-api", await File.ReadAllTextAsync(ApiTokenPath));
    }

    [Fact]
    public async Task Saved_api_token_survives_without_options_json()
    {
        var store = CreateStore();

        await store.SaveTokenAsync(" api-token ");
        File.Delete(OptionsPath);

        var token = await store.GetTokenAsync();
        var state = await store.GetStateAsync();

        Assert.Equal("api-token", token);
        Assert.True(state.HasToken);
        Assert.Equal("api-token", await File.ReadAllTextAsync(ApiTokenPath));
        Assert.True(File.Exists(ApiTokenValidatedAtPath));
    }

    [Fact]
    public async Task Clear_token_removes_persistent_and_legacy_secret_values()
    {
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            OptionsPath,
            """{"cloudflare_api_token":"legacy-api","cloudflare_api_token_validated_at":"2026-05-29T00:00:00Z","cloudflare_tunnel_token":"legacy-tunnel","advanced_docker_discovery":true}""");
        var store = CreateStore();
        await store.SaveTokenAsync("api-token");
        await store.SaveTunnelTokenAsync("tunnel-token");

        await store.ClearTokenAsync();

        Assert.False(File.Exists(ApiTokenPath));
        Assert.False(File.Exists(ApiTokenValidatedAtPath));
        Assert.False(File.Exists(TunnelTokenPath));
        var optionsJson = await File.ReadAllTextAsync(OptionsPath);
        Assert.DoesNotContain("cloudflare_api_token", optionsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudflare_api_token_validated_at", optionsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudflare_tunnel_token", optionsJson, StringComparison.Ordinal);
        Assert.Contains("advanced_docker_discovery", optionsJson, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private string OptionsPath => Path.Combine(tempRoot, "options.json");

    private string ApiTokenPath => Path.Combine(tempRoot, "cloudflare-api-token");

    private string ApiTokenValidatedAtPath => Path.Combine(tempRoot, "cloudflare-api-token.validated-at");

    private string TunnelTokenPath => Path.Combine(tempRoot, "cloudflared-token");

    private JsonCloudflareApiTokenStore CreateStore() =>
        new(Options.Create(new EdgeGatewayCoreOptions
        {
            DataRoot = tempRoot,
            OptionsJsonPath = OptionsPath
        }));
}
