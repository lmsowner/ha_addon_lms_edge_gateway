namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewayRelayProvisioningService
{
    Task<EdgeGatewayRelayProvisioningResult> ProvisionRelayAsync(
        string domainName,
        bool replaceExistingDnsRecord = false,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayRelayRemovalResult> RemoveRelayAsync(
        string domainName,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayRelayValidationResult> ValidateRelayAsync(
        string domainName,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayRelayValidationResult> RepairRelayAsync(
        string domainName,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayApplicationSaveResult> AddApplicationAsync(
        string domainName,
        string name,
        string hostLabel,
        string targetScheme,
        string targetHost,
        int targetPort,
        string accessPolicy,
        string targetPathPrefix = "",
        bool isEnabled = true,
        string allowKnownIps = "",
        string allowedUsers = "",
        string allowedGroups = "",
        bool allowLanOnly = false,
        string notes = "",
        bool? usePublicHostHeader = null,
        bool? stripForwardedFor = null,
        bool? skipUpstreamTlsVerification = null,
        string temporaryIpApprovalRecipients = "",
        string temporaryIpApprovalAllowedCountryCodes = "",
        bool temporaryIpApprovalUseNotFoundResponse = false,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayApplicationSaveResult> UpdateApplicationAsync(
        Guid applicationId,
        string name,
        string hostLabel,
        string targetScheme,
        string targetHost,
        int targetPort,
        string accessPolicy,
        string targetPathPrefix = "",
        string allowKnownIps = "",
        string allowedUsers = "",
        string allowedGroups = "",
        bool allowLanOnly = false,
        string notes = "",
        bool? usePublicHostHeader = null,
        bool? stripForwardedFor = null,
        bool? skipUpstreamTlsVerification = null,
        string temporaryIpApprovalRecipients = "",
        string temporaryIpApprovalAllowedCountryCodes = "",
        bool temporaryIpApprovalUseNotFoundResponse = false,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayApplicationSaveResult> PublishApplicationAsync(
        Guid applicationId,
        bool replaceExistingDnsRecord = false,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayApplicationSaveResult> SetApplicationEnabledAsync(
        Guid applicationId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayApplicationSaveResult> RemoveApplicationAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayApplicationTestResult> TestApplicationAsync(
        Guid applicationId,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayCaddyConfigurationResult> RefreshCaddyConfigurationAsync(
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayCaddyConfigurationResult> RefreshPublishedConfigurationAsync(
        CancellationToken cancellationToken = default);
}
