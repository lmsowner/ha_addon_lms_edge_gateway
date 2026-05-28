namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayStatusService
{
    Task<EdgeGatewayRuntimeStatus> GetStatusAsync(string? ingressPath = null, CancellationToken cancellationToken = default);
}
