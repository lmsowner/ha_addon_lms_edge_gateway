namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayTemporaryIpApprovalService
{
    Task<IReadOnlyList<TrustedIpAddressViewModel>> ListTrustedIpAddressesAsync(
        CancellationToken cancellationToken = default);

    Task<bool> RevokeTrustedIpAddressAsync(
        Guid grantId,
        CancellationToken cancellationToken = default);

    Task<TemporaryIpApprovalEvaluationResult> EvaluateAsync(
        PublishedApplicationDefinition route,
        TemporaryIpApprovalCheckContext context,
        CancellationToken cancellationToken = default);

    Task<TemporaryIpApprovalCompletionResult> ApproveAsync(
        string token,
        CancellationToken cancellationToken = default);
}
