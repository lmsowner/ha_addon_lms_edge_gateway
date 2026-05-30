using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class CloudflareTunnelService(
    ICloudflareClient client,
    IOptions<EdgeGatewayCoreOptions> options) : ICloudflareTunnelService
{
    public async Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        string apiToken,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var results = await client.GetAllPagesAsync<CloudflareTunnelDto>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel",
            cancellationToken: cancellationToken);

        return results
            .Select(item => item.ToModel(options.Value.ManagedTunnelNamePrefix))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudflareTunnel> CreateTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelName,
        CancellationToken cancellationToken = default)
    {
        var result = await client.PostAsync<object, CloudflareTunnelDto>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel",
            new
            {
                name = tunnelName,
                config_src = "cloudflare"
            },
            cancellationToken);

        return result.ToModel(options.Value.ManagedTunnelNamePrefix);
    }

    public async Task<CloudflareTunnel?> GetTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await client.GetAsync<CloudflareTunnelDto>(
                apiToken,
                $"accounts/{accountId}/cfd_tunnel/{tunnelId}",
                cancellationToken: cancellationToken);

            return result.ToModel(options.Value.ManagedTunnelNamePrefix);
        }
        catch (CloudflareApiException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<CloudflareTunnelConfiguration> GetConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await client.GetAsync<CloudflareTunnelConfigurationResponseDto>(
                apiToken,
                $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
                cancellationToken: cancellationToken);

            return result.ToModel();
        }
        catch (CloudflareApiException exception) when (exception.StatusCode == 404)
        {
            return new CloudflareTunnelConfiguration([]);
        }
    }

    public Task UpdateConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CloudflareTunnelConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var ingress = configuration.Routes
            .Select(route => string.IsNullOrWhiteSpace(route.Hostname)
                ? new Dictionary<string, object?>
                {
                    ["service"] = route.Service
                }
                : new Dictionary<string, object?>
                {
                    ["hostname"] = route.Hostname,
                    ["service"] = route.Service,
                    ["originRequest"] = BuildOriginRequest(route.OriginRequest)
                })
            .ToArray();

        return client.PutAsync<object, CloudflareTunnelConfigurationResponseDto>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
            new
            {
                config = new
                {
                    ingress
                }
            },
            cancellationToken);
    }

    public Task<string> GetTunnelTokenAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default) =>
        client.GetAsync<string>(
            apiToken,
            $"accounts/{accountId}/cfd_tunnel/{tunnelId}/token",
            cancellationToken: cancellationToken);

    public Task DeleteTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"accounts/{accountId}/cfd_tunnel/{tunnelId}", cancellationToken);

    public Task DeleteTunnelConnectionsAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default) =>
        client.DeleteAsync(apiToken, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/connections", cancellationToken);

    private static Dictionary<string, object?> BuildOriginRequest(CloudflareOriginRequestSettings settings)
    {
        var originRequest = new Dictionary<string, object?>
        {
            ["noTLSVerify"] = settings.NoTlsVerify,
            ["tlsTimeout"] = Math.Max(1, settings.TlsTimeoutSeconds),
            ["http2Origin"] = settings.Http2Origin,
            ["matchSNItoHost"] = settings.MatchSniToHost,
            ["disableChunkedEncoding"] = settings.DisableChunkedEncoding,
            ["connectTimeout"] = Math.Max(1, settings.ConnectTimeoutSeconds),
            ["noHappyEyeballs"] = settings.NoHappyEyeballs,
            ["keepAliveTimeout"] = Math.Max(1, settings.KeepAliveTimeoutSeconds),
            ["keepAliveConnections"] = Math.Max(0, settings.KeepAliveConnections),
            ["tcpKeepAlive"] = Math.Max(1, settings.TcpKeepAliveSeconds)
        };

        if (!string.IsNullOrWhiteSpace(settings.OriginServerName))
        {
            originRequest["originServerName"] = settings.OriginServerName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.CertificateAuthorityPool))
        {
            originRequest["caPool"] = settings.CertificateAuthorityPool.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.HttpHostHeader))
        {
            originRequest["httpHostHeader"] = settings.HttpHostHeader.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.ProxyType))
        {
            originRequest["proxyType"] = settings.ProxyType.Trim();
        }

        return originRequest;
    }
}
