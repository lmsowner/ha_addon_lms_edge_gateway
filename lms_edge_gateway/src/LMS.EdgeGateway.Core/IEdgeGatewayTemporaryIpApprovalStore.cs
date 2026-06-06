namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayTemporaryIpApprovalStore
{
    Task<TemporaryIpApprovalConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TemporaryIpApprovalConfiguration configuration, CancellationToken cancellationToken = default);
}
