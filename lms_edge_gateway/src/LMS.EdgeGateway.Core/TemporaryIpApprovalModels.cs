namespace LMS.EdgeGateway.Core;

public sealed record TemporaryIpApprovalConfiguration(
    IReadOnlyList<TemporaryIpApprovalRequest> Requests,
    IReadOnlyList<TemporaryIpApprovalGrant> Grants,
    DateTimeOffset UpdatedAtUtc)
{
    public static TemporaryIpApprovalConfiguration Empty { get; } = new([], [], DateTimeOffset.UtcNow);
}

public sealed record TemporaryIpApprovalRequest(
    Guid Id,
    Guid RouteId,
    string RouteName,
    string PublicHostname,
    string TargetPathPrefix,
    string SourceIp,
    string CountryCode,
    string UserAgent,
    string RequestedUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastEmailSentUtc,
    int EmailSendCount,
    string ApprovalTokenHash,
    DateTimeOffset? ApprovalTokenExpiresAtUtc,
    DateTimeOffset? ApprovedUtc,
    string LastEmailStatus);

public sealed record TemporaryIpApprovalGrant(
    Guid Id,
    Guid RouteId,
    string RouteName,
    string PublicHostname,
    string TargetPathPrefix,
    string SourceIp,
    string CountryCode,
    string UserAgent,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record TrustedIpAddressViewModel(
    Guid Id,
    Guid RouteId,
    string RouteName,
    string PublicHostname,
    string TargetPathPrefix,
    string SourceIp,
    string CountryCode,
    string UserAgent,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record TemporaryIpApprovalCheckContext(
    string RequestedHost,
    string RequestedPath,
    string RequestedUrl,
    string SourceIp,
    string CountryCode,
    string UserAgent);

public sealed record TemporaryIpApprovalEvaluationResult(
    bool IsAllowed,
    string Reason,
    bool EmailAttempted = false,
    bool EmailSucceeded = false);

public sealed record TemporaryIpApprovalCompletionResult(
    bool Success,
    string Title,
    string Message,
    string SourceIp = "",
    string CountryCode = "",
    string RouteName = "",
    string PublicHostname = "",
    string ApprovedUrl = "",
    DateTimeOffset? IdleExpiresAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null);
