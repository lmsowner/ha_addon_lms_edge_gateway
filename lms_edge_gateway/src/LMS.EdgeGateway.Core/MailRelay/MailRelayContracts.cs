namespace LMS.EdgeGateway.Core;

public sealed record MailRelaySetupRequest(
    string CloudflareZoneId,
    string RelayHostname,
    string SendingDomain,
    string DkimSelector,
    string ApplicationName,
    string ApplicationUsername,
    bool AllowTailscale,
    bool AllowTrustedLan);

public sealed record MailRelayLegacySubmissionRequest(
    bool Enabled,
    IReadOnlyList<string> ListenAddresses,
    IReadOnlyList<string> AllowedNetworks);

public sealed record MailRelayLegacySubmissionResult(
    bool Success,
    MailRelayConfiguration? Configuration,
    string Summary);

public sealed record MailRelayRemovalRequest(
    bool RemoveContainer,
    bool RemoveApplicationCredentials,
    bool RemoveManagedDnsRecords,
    bool RemoveDkimKeys,
    bool Confirmed);

public sealed record MailRelayRemovalResult(
    bool Success,
    MailRelayConfiguration? Configuration,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    string Summary);

public enum MailRelaySetupChangeKind
{
    Create = 0,
    Update = 1,
    Keep = 2,
    Blocked = 3
}

public sealed record MailRelaySetupChange(
    string Purpose,
    string RecordType,
    string RecordName,
    string ProposedValue,
    MailRelaySetupChangeKind Kind,
    string Detail);

public enum MailRelayExistingProvider
{
    NoneDetected = 0,
    Microsoft365 = 1,
    GoogleWorkspace = 2,
    ExistingMailProvider = 3
}

public sealed record MailRelayExistingEmailConfiguration(
    string Domain,
    MailRelayExistingProvider Provider,
    IReadOnlyList<string> MxRecords,
    string? ExistingSpf,
    string ProposedSpf,
    int SpfDnsLookupTerms,
    IReadOnlyList<string> ExistingDkimRecords,
    string? ExistingDmarc,
    string? ExistingDmarcPolicy,
    MailRelayDeliveryMode DeliveryMode);

public sealed record MailRelaySetupPreview(
    MailRelaySetupRequest Request,
    MailRelayPreflightResult Preflight,
    IReadOnlyList<MailRelaySetupChange> DnsChanges,
    MailRelayExistingEmailConfiguration ExistingEmailConfiguration,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanInstall => Preflight.CanConfigure && Errors.Count == 0;
}

public enum MailRelaySetupStepState
{
    Pending = 0,
    Running = 1,
    Complete = 2,
    Failed = 3
}

public sealed record MailRelaySetupProgressUpdate(
    string Key,
    string Label,
    MailRelaySetupStepState State,
    string Detail);

public sealed record MailRelaySetupResult(
    bool Success,
    string RelayHostname,
    string SubmissionHost,
    int SubmissionPort,
    string Username,
    string? GeneratedPassword,
    string SendingDomain,
    string DkimSelector,
    bool OpenRelayTestPassed,
    IReadOnlyList<MailRelaySetupProgressUpdate> Steps,
    string Summary);

public enum MailRelayProvisioningJobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

public sealed record MailRelayProvisioningJobSnapshot(
    Guid Id,
    MailRelaySetupRequest Request,
    MailRelayProvisioningJobStatus Status,
    IReadOnlyList<MailRelaySetupProgressUpdate> Steps,
    MailRelaySetupResult? Result,
    string Summary,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc)
{
    public bool IsTerminal => Status is MailRelayProvisioningJobStatus.Succeeded or MailRelayProvisioningJobStatus.Failed;
}

public enum MailRelayPreflightCheckState
{
    NotRun = 0,
    Pass = 1,
    Warning = 2,
    Failed = 3,
    NotAvailable = 4
}

public static class MailRelayPreflightCheckKeys
{
    public const string EdgeGateway = "edge-gateway";
    public const string CloudflareAuthentication = "cloudflare-authentication";
    public const string CloudflareZone = "cloudflare-zone";
    public const string DnsList = "dns-list";
    public const string DnsEdit = "dns-edit";
    public const string PublicIpv4 = "public-ipv4";
    public const string OutboundSmtp = "outbound-smtp";
    public const string ReverseDns = "reverse-dns";
    public const string MailRuntime = "mail-runtime";
}

public sealed record MailRelayPreflightCheck(
    string Key,
    string Label,
    MailRelayPreflightCheckState State,
    string Value,
    string Detail);

public sealed record MailRelayCloudflareZoneOption(
    string ZoneId,
    string ZoneName,
    string Status,
    bool Paused,
    bool IsSavedDefault);

public sealed record MailRelayPreflightResult(
    string CloudflareZoneId,
    string CloudflareZoneName,
    IReadOnlyList<MailRelayCloudflareZoneOption> AvailableZones,
    string SuggestedRelayHostname,
    string PublicIpAddress,
    string ReverseDnsHostname,
    IReadOnlyList<MailRelayPreflightCheck> Checks,
    bool DnsEditWasTested,
    DateTimeOffset CheckedAtUtc)
{
    public MailRelayPreflightCheck GetCheck(string key) =>
        Checks.First(check => check.Key.Equals(key, StringComparison.Ordinal));

    public bool CloudflareZonesAvailable =>
        GetCheck(MailRelayPreflightCheckKeys.EdgeGateway).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.CloudflareAuthentication).State == MailRelayPreflightCheckState.Pass &&
        AvailableZones.Count > 0;

    public bool CloudflareDnsReady =>
        GetCheck(MailRelayPreflightCheckKeys.CloudflareAuthentication).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.CloudflareZone).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.DnsList).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.DnsEdit).State == MailRelayPreflightCheckState.Pass;

    public bool HostSuitable =>
        GetCheck(MailRelayPreflightCheckKeys.PublicIpv4).State == MailRelayPreflightCheckState.Pass &&
        GetCheck(MailRelayPreflightCheckKeys.MailRuntime).State is MailRelayPreflightCheckState.Pass or MailRelayPreflightCheckState.Warning;

    public bool CanConfigure => CloudflareDnsReady && HostSuitable;
}

public sealed record MailRelayDashboardViewModel(
    MailRelayOperationalStatus Status,
    string StatusSummary,
    MailRelayConfiguration? Configuration,
    IReadOnlyList<MailRelayDomain> Domains,
    IReadOnlyList<MailRelayClient> Clients,
    MailRelayPreflightResult Preflight);

public sealed record MailRelayPublicIpv4DetectionResult(
    bool Success,
    string Address,
    string Detail);

public sealed record MailRelayPublicIpMonitorSettingsRequest(
    bool Enabled,
    int CheckIntervalMinutes);

public sealed record MailRelayPublicIpDnsCheck(
    string Purpose,
    string RecordName,
    MailRelayDnsStatus Status,
    bool Changed,
    string Detail);

public sealed record MailRelayPublicIpSyncResult(
    bool Success,
    bool PublicIpChanged,
    string PreviousPublicIp,
    string CurrentPublicIp,
    MailRelayPublicIpMonitorStatus Status,
    IReadOnlyList<MailRelayPublicIpDnsCheck> DnsChecks,
    string Summary,
    DateTimeOffset CheckedAtUtc);

public sealed record MailRelayClientSaveRequest(
    Guid? ClientId,
    string Name,
    string Username,
    string Password,
    IReadOnlyList<Guid> AllowedDomainIds);

public sealed record MailRelayClientSaveResult(
    bool Success,
    MailRelayClient? Client,
    bool PasswordChanged,
    string Summary);

public enum MailRelayTestStatus
{
    Failed = 0,
    Rejected = 1,
    Queued = 2,
    Sent = 3,
    Deferred = 4,
    Bounced = 5
}

public sealed record MailRelayTestRequest(
    Guid ClientId,
    string FromAddress,
    string RecipientAddress);

public sealed record MailRelayTestResult(
    MailRelayTestStatus Status,
    bool ClientPolicyValidated,
    bool AcceptedByRelay,
    string ApplicationName,
    string FromAddress,
    string RecipientAddress,
    string? QueueId,
    string? DestinationServer,
    string SmtpResponse,
    string Summary,
    DateTimeOffset TestedAtUtc,
    bool DmarcIdentityAligned = false,
    IReadOnlyList<string>? LogLines = null)
{
    public bool Passed =>
        ClientPolicyValidated &&
        AcceptedByRelay &&
        Status is MailRelayTestStatus.Queued or MailRelayTestStatus.Sent;

    public IReadOnlyList<string> Logs => LogLines ?? [];
}

public enum MailRelayLogSeverity
{
    Info = 0,
    Sent = 1,
    Warning = 2,
    Error = 3
}

public sealed record MailRelayLogEntry(
    string Timestamp,
    string Service,
    string? QueueId,
    string Detail,
    MailRelayLogSeverity Severity);

public sealed record MailRelayLogSnapshot(
    bool Available,
    string Path,
    string Summary,
    IReadOnlyList<string> Lines,
    IReadOnlyList<MailRelayLogEntry>? Entries = null,
    IReadOnlyList<string>? QueueLines = null)
{
    public IReadOnlyList<MailRelayLogEntry> Events => Entries ?? [];
    public IReadOnlyList<string> Queue => QueueLines ?? [];
}
