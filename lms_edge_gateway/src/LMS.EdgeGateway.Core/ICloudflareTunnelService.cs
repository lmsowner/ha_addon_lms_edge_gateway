namespace LMS.EdgeGateway.Core;

public interface ICloudflareTunnelService
{
    Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        string apiToken,
        string accountId,
        CancellationToken cancellationToken = default);

    Task<CloudflareTunnel> CreateTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelName,
        CancellationToken cancellationToken = default);

    Task<CloudflareTunnel?> GetTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);

    Task<CloudflareTunnelConfiguration> GetConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);

    Task UpdateConfigurationAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CloudflareTunnelConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<string> GetTunnelTokenAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);

    Task DeleteTunnelAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);

    Task DeleteTunnelConnectionsAsync(
        string apiToken,
        string accountId,
        string tunnelId,
        CancellationToken cancellationToken = default);
}
