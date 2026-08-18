namespace LMS.EdgeGateway.Core;

public sealed record EdgeGatewayAuthSessionFlushResult(
    bool Success,
    string Message,
    int EmailApprovalGrantsCleared,
    int EmailApprovalRequestsCleared,
    int AccessCheckPagesCleared,
    bool InMemoryAuthCacheCleared);

public interface IEdgeGatewayAuthSessionFlushService
{
    Task<EdgeGatewayAuthSessionFlushResult> FlushAsync(CancellationToken cancellationToken = default);
}
