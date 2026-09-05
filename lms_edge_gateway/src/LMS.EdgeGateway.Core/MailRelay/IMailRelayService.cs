namespace LMS.EdgeGateway.Core;

public interface IMailRelayService
{
    Task<MailRelayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<MailRelayPreflightResult> RecheckPreflightAsync(string? cloudflareZoneId = null, CancellationToken cancellationToken = default);
    Task<MailRelaySetupPreview> PreviewSetupAsync(MailRelaySetupRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayProvisioningJobSnapshot> StartSetupAsync(MailRelaySetupRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayProvisioningJobSnapshot?> GetSetupJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<MailRelayProvisioningJobSnapshot?> GetLatestSetupJobAsync(CancellationToken cancellationToken = default);
    Task<MailRelayClientSaveResult> SaveClientAsync(MailRelayClientSaveRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayClientSaveResult> DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default);
    string GenerateClientPassword();
    Task<MailRelayDomainPreview> PreviewDomainAsync(MailRelayDomainRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayDomainMutationResult> AddDomainAsync(MailRelayDomainRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayDomainDetail?> GetDomainDetailAsync(Guid domainId, CancellationToken cancellationToken = default);
    Task<MailRelayDomainMutationResult> DeleteDomainAsync(Guid domainId, bool removeManagedDns, CancellationToken cancellationToken = default);
    Task<MailRelayTestResult> SendTestAsync(MailRelayTestRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayLogSnapshot> GetMailLogAsync(string? queueId = null, string? messageId = null, CancellationToken cancellationToken = default);
    Task<MailRelayQueueResult> ClearMailQueueAsync(CancellationToken cancellationToken = default);
    Task<MailRelayLegacySubmissionResult> UpdateLegacySubmissionAsync(MailRelayLegacySubmissionRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayRemovalResult> RemoveMailRelayAsync(MailRelayRemovalRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayConfiguration> UpdatePublicIpMonitorSettingsAsync(MailRelayPublicIpMonitorSettingsRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayPublicIpSyncResult> CheckPublicIpNowAsync(CancellationToken cancellationToken = default);
}

public interface IMailRelayProvisioningService
{
    Task<MailRelaySetupPreview> PreviewAsync(MailRelaySetupRequest request, CancellationToken cancellationToken = default);
    Task<MailRelaySetupResult> ProvisionAsync(
        MailRelaySetupRequest request,
        IProgress<MailRelaySetupProgressUpdate>? progress,
        CancellationToken cancellationToken);
    Task<MailRelayLegacySubmissionResult> ConfigureLegacySubmissionAsync(
        MailRelayLegacySubmissionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
    Task<MailRelayRemovalResult> RemoveAsync(
        MailRelayRemovalRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
    Task<MailRelayPublicIpSyncResult> SynchronizePublicIpAsync(string detectedPublicIp, CancellationToken cancellationToken = default);
    Task<MailRelayDomainPreview> PreviewDomainAsync(MailRelayDomainRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayDomainMutationResult> AddDomainAsync(MailRelayDomainRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayDomainDetail?> GetDomainDetailAsync(Guid domainId, CancellationToken cancellationToken = default);
    Task<MailRelayDomainMutationResult> DeleteDomainAsync(Guid domainId, bool removeManagedDns, CancellationToken cancellationToken = default);
}

public interface IMailRelayProvisioningQueue
{
    Task<MailRelayProvisioningJobSnapshot> EnqueueAsync(MailRelaySetupRequest request, CancellationToken cancellationToken = default);
    MailRelayProvisioningJobSnapshot? GetJob(Guid jobId);
    MailRelayProvisioningJobSnapshot? GetLatestJob();
}

public interface IMailRelayPreflightService
{
    Task<MailRelayPreflightResult> InspectAsync(
        bool verifyDnsEdit,
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default);
    Task<MailRelayPublicIpv4DetectionResult> DetectPublicIpv4Async(CancellationToken cancellationToken = default);
}

public interface IMailRelayClientService
{
    string GeneratePassword();
    Task<MailRelayClientSaveResult> SaveAsync(MailRelayClientSaveRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayClientSaveResult> DeleteAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public interface IMailRelayTestService
{
    Task<MailRelayTestResult> SendAsync(MailRelayTestRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayLogSnapshot> GetLogAsync(string? queueId = null, string? messageId = null, CancellationToken cancellationToken = default);
    Task<MailRelayQueueResult> ClearQueueAsync(CancellationToken cancellationToken = default);
}

public interface IMailRelayHostCommand
{
    Task<MailRelayHostCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        byte[]? standardInput = null,
        TimeSpan? timeout = null);
}

public sealed record MailRelayHostCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IMailRelayPublicIpMonitorService
{
    Task<MailRelayConfiguration> SaveSettingsAsync(MailRelayPublicIpMonitorSettingsRequest request, CancellationToken cancellationToken = default);
    Task<MailRelayPublicIpSyncResult> CheckNowAsync(CancellationToken cancellationToken = default);
}

public interface IMailRelaySecretStore
{
    Task<string> SaveAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string? reference, CancellationToken cancellationToken = default);
}
