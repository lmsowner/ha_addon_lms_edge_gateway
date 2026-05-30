namespace LMS.EdgeGateway.Core;

public interface ILocalHttpServiceDiscoveryService
{
    Task<IReadOnlyList<LocalHttpServiceEndpoint>> GetCachedAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> ValidateCachedAsync(
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default);
}
