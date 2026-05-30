namespace LMS.EdgeGateway.Core;

public interface ICloudflareApiTokenStore
{
    Task<CloudflareApiTokenState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    Task<string?> GetTunnelTokenAsync(CancellationToken cancellationToken = default);
    Task SaveTokenAsync(string token, CancellationToken cancellationToken = default);
    Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken = default);
    Task ClearTunnelTokenAsync(CancellationToken cancellationToken = default);
    Task ClearTokenAsync(CancellationToken cancellationToken = default);
}

public sealed record CloudflareApiTokenState(
    bool HasToken,
    string OptionsPath);

public interface ICloudflareApiTokenValidator
{
    Task<CloudflareApiTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default);
}

public sealed record CloudflareApiTokenValidationResult(
    bool IsValid,
    string Message);
