namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayTemporaryIpApprovalService
{
    Task<TemporaryIpApprovalEvaluationResult> EvaluateAsync(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        CancellationToken cancellationToken = default);

    Task<TemporaryIpApprovalCompletionResult> ApproveAsync(
        string token,
        CancellationToken cancellationToken = default);
}
