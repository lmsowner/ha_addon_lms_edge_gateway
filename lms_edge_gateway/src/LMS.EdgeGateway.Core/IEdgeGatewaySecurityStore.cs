namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewaySecurityStore
{
    Task<EdgeGatewaySecurityConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EdgeGatewaySecurityConfiguration configuration, CancellationToken cancellationToken = default);
}
