using LMS.EdgeGateway.Core;
using Microsoft.Extensions.Caching.Memory;

namespace HA.LMS.EdgeGateway.Services;

public sealed class EdgeGatewayAuthSessionFlushService(
    IEdgeGatewayTemporaryIpApprovalService temporaryIpApprovalService,
    IEdgeGatewayAccessCheckPageStore accessCheckPageStore,
    IMemoryCache memoryCache) : IEdgeGatewayAuthSessionFlushService
{
    public async Task<EdgeGatewayAuthSessionFlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var emailApproval = await temporaryIpApprovalService.ClearAllAsync(cancellationToken);
        var accessCheckPagesCleared = accessCheckPageStore.ClearAll();
        memoryCache.Compact(1.0);

        var total = emailApproval.GrantsCleared + emailApproval.RequestsCleared + accessCheckPagesCleared;
        var message = total == 0
            ? "No active email approvals, access check pages, or in-memory login/passkey states were cached."
            : $"Cleared {emailApproval.GrantsCleared} email approval grant(s), {emailApproval.RequestsCleared} pending approval request(s), {accessCheckPagesCleared} access check page(s), and in-memory login/passkey ceremony state.";

        return new EdgeGatewayAuthSessionFlushResult(
            true,
            message,
            emailApproval.GrantsCleared,
            emailApproval.RequestsCleared,
            accessCheckPagesCleared,
            InMemoryAuthCacheCleared: true);
    }
}
