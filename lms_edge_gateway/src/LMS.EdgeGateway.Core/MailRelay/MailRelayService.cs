namespace LMS.EdgeGateway.Core;

public sealed class MailRelayService(
    IMailRelayStore store,
    IMailRelayPreflightService preflightService,
    IMailRelayProvisioningService provisioningService,
    IMailRelayProvisioningQueue provisioningQueue,
    IMailRelayTestService testService,
    IMailRelayClientService clientService,
    IMailRelayPublicIpMonitorService publicIpMonitorService) : IMailRelayService
{
    public async Task<MailRelayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var configurationTask = store.GetConfigurationAsync(cancellationToken);
        var domainsTask = store.ListDomainsAsync(cancellationToken);
        var clientsTask = store.ListClientsAsync(cancellationToken);
        var preflightTask = preflightService.InspectAsync(false, cancellationToken: cancellationToken);

        await Task.WhenAll(configurationTask, domainsTask, clientsTask, preflightTask);

        var configuration = await configurationTask;
        var preflight = await preflightTask;
        var status = configuration is null
            ? MailRelayOperationalStatus.NotConfigured
            : configuration.Enabled
                ? MailRelayOperationalStatus.Healthy
                : MailRelayOperationalStatus.NotConfigured;

        var summary = configuration is null
            ? preflight.CanConfigure
                ? "This Home Assistant host is suitable. Mail Relay has not been configured yet."
                : "Complete the required preflight checks before configuring Mail Relay."
            : configuration.Enabled
                ? $"Mail Relay is configured at {configuration.RelayHostname}:{configuration.SubmissionPort}."
                : "Mail Relay configuration is saved but disabled.";

        return new MailRelayDashboardViewModel(
            status,
            summary,
            configuration,
            await domainsTask,
            await clientsTask,
            preflight);
    }

    public Task<MailRelayPreflightResult> RecheckPreflightAsync(
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default) =>
        preflightService.InspectAsync(true, cloudflareZoneId, cancellationToken);

    public Task<MailRelaySetupPreview> PreviewSetupAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default) =>
        provisioningService.PreviewAsync(request, cancellationToken);

    public Task<MailRelayProvisioningJobSnapshot> StartSetupAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default) =>
        provisioningQueue.EnqueueAsync(request, cancellationToken);

    public Task<MailRelayProvisioningJobSnapshot?> GetSetupJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(provisioningQueue.GetJob(jobId));
    }

    public Task<MailRelayProvisioningJobSnapshot?> GetLatestSetupJobAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(provisioningQueue.GetLatestJob());
    }

    public Task<MailRelayClientSaveResult> SaveClientAsync(
        MailRelayClientSaveRequest request,
        CancellationToken cancellationToken = default) =>
        clientService.SaveAsync(request, cancellationToken);

    public string GenerateClientPassword() => clientService.GeneratePassword();

    public Task<MailRelayTestResult> SendTestAsync(
        MailRelayTestRequest request,
        CancellationToken cancellationToken = default) =>
        testService.SendAsync(request, cancellationToken);

    public Task<MailRelayLogSnapshot> GetMailLogAsync(
        string? queueId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default) =>
        testService.GetLogAsync(queueId, messageId, cancellationToken);

    public Task<MailRelayLegacySubmissionResult> UpdateLegacySubmissionAsync(
        MailRelayLegacySubmissionRequest request,
        CancellationToken cancellationToken = default) =>
        provisioningService.ConfigureLegacySubmissionAsync(request, null, cancellationToken);

    public Task<MailRelayRemovalResult> RemoveMailRelayAsync(
        MailRelayRemovalRequest request,
        CancellationToken cancellationToken = default) =>
        provisioningService.RemoveAsync(request, null, cancellationToken);

    public Task<MailRelayConfiguration> UpdatePublicIpMonitorSettingsAsync(
        MailRelayPublicIpMonitorSettingsRequest request,
        CancellationToken cancellationToken = default) =>
        publicIpMonitorService.SaveSettingsAsync(request, cancellationToken);

    public Task<MailRelayPublicIpSyncResult> CheckPublicIpNowAsync(CancellationToken cancellationToken = default) =>
        publicIpMonitorService.CheckNowAsync(cancellationToken);
}
