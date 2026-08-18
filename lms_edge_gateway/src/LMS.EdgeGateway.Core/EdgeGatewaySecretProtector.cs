using Microsoft.AspNetCore.DataProtection;

namespace LMS.EdgeGateway.Core;

public interface IEdgeGatewaySecretProtector
{
    string Protect(string secret);
    string Unprotect(string protectedSecret);
}

public sealed class EdgeGatewaySecretProtector(IDataProtectionProvider dataProtectionProvider) : IEdgeGatewaySecretProtector
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("LMS.EdgeGateway.Security.Secrets.v1");

    public string Protect(string secret) =>
        string.IsNullOrWhiteSpace(secret) ? string.Empty : protector.Protect(secret.Trim());

    public string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return string.Empty;
        }

        return protector.Unprotect(protectedSecret);
    }
}
