namespace LMS.EdgeGateway.Core;

public interface IWellKnownServiceManager
{
    IReadOnlyList<WellKnownTemplateDefinition> GetTemplates();

    Task<WellKnownConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<WellKnownServiceSaveResult> SaveAsync(
        WellKnownServiceSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<WellKnownServiceSaveResult> CreateSecurityTxtAsync(
        SecurityTxtTemplateRequest request,
        CancellationToken cancellationToken = default);

    Task<WellKnownServiceSaveResult> CreateTeslaFleetAsync(
        string domain,
        string displayName = "Tesla Fleet public key",
        CancellationToken cancellationToken = default);

    Task<WellKnownServiceSaveResult> PublishAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<WellKnownServiceSaveResult> SetEnabledAsync(
        Guid serviceId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<WellKnownDeleteResult> DeleteAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<WellKnownVerificationResult> VerifyAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    string BuildPublicUrl(WellKnownService service);
}
