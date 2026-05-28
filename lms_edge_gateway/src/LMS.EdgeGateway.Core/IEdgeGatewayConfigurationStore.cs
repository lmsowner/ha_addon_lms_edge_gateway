namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayConfigurationStore
{
    Task<EdgeGatewayConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EdgeGatewayConfiguration configuration, CancellationToken cancellationToken = default);
}
