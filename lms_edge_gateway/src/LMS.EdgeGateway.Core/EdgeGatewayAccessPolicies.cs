namespace LMS.EdgeGateway.Core;

public static class EdgeGatewayAccessPolicies
{
    public const string MfaPasskey = "MFA/Passkey";
    public const string PassThrough = "Pass Through";
    public const string TemporaryIpApproval = "Email approve IP";

    public static bool IsBlocked(string? accessPolicy) =>
        (accessPolicy ?? string.Empty).Contains("block", StringComparison.OrdinalIgnoreCase);

    public static bool IsPassThrough(string? accessPolicy)
    {
        var value = (accessPolicy ?? string.Empty).Trim();
        return value.Equals(PassThrough, StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Pass-through", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("PassThrough", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Public", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTemporaryIpApproval(string? accessPolicy)
    {
        var value = (accessPolicy ?? string.Empty).Trim();
        return value.Equals(TemporaryIpApproval, StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Temporary IP Approval", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Email Approved IP", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("EmailApproveIp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool RequiresLmsAuthentication(string? accessPolicy) =>
        !IsBlocked(accessPolicy) && !IsPassThrough(accessPolicy);
}
