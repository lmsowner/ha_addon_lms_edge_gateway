namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewaySecurityService
{
    Task<SecuritySettingsPageViewModel> GetPageAsync(CancellationToken cancellationToken = default);
    Task<SecurityMessagingSettingsEditor> GetMessagingEditorAsync(CancellationToken cancellationToken = default);
    Task SaveMessagingSettingsAsync(SecurityMessagingSettingsEditor editor, CancellationToken cancellationToken = default);
    Task<SecurityMessagingTestResult> SendMessagingTestAsync(
        SecurityMessagingSettingsEditor editor,
        string recipientAddress,
        CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel> CreateUserAsync(SecurityUserEditor editor, string? loginUrl = null, CancellationToken cancellationToken = default);
    Task<SecurityUserEditor> GetUserEditorAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SaveUserAsync(SecurityUserEditor editor, CancellationToken cancellationToken = default);
    Task SetUserEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default);
    Task<SecurityUserProvisioningViewModel> ResetUserOtpAsync(Guid userId, string? loginUrl = null, CancellationToken cancellationToken = default);
    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> RevokeTrustedIpAddressAsync(Guid trustedIpId, CancellationToken cancellationToken = default);
    Task<EdgeGatewayAuthSessionFlushResult> FlushAuthSessionsAsync(CancellationToken cancellationToken = default);
    Task<SecurityAuthenticationResult> ValidateOtpAsync(string email, string otpCode, CancellationToken cancellationToken = default);
}
