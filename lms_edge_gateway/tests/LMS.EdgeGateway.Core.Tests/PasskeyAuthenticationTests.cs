using Xunit;
using System.Text.Json;
using HA.LMS.EdgeGateway.Services;
using LMS.EdgeGateway.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class PasskeyAuthenticationTests
{
    [Fact]
    public async Task Login_offers_device_account_selection_without_an_email_or_credential_list()
    {
        var service = CreateService(EdgeGatewaySecurityConfiguration.Empty);
        var first = await service.BuildLoginOptionsAsync(Request(), default);
        var second = await service.BuildLoginOptionsAsync(Request(), default);

        Assert.True(first.Succeeded);
        using var options = JsonDocument.Parse(first.OptionsJson!);
        using var nextOptions = JsonDocument.Parse(second.OptionsJson!);
        Assert.Equal(0, options.RootElement.GetProperty("allowCredentials").GetArrayLength());
        Assert.Equal("required", options.RootElement.GetProperty("userVerification").GetString());
        Assert.NotEqual(options.RootElement.GetProperty("challenge").GetString(),
            nextOptions.RootElement.GetProperty("challenge").GetString());
        Assert.NotEqual(first.StateId, second.StateId);
    }

    [Fact]
    public async Task New_passkeys_require_discoverable_credentials_and_user_verification()
    {
        var user = User();
        var service = CreateService(EdgeGatewaySecurityConfiguration.Empty with { Users = [user] });
        var result = await service.BuildRegistrationOptionsAsync(user.Id, "Test key", Request(), default);

        Assert.True(result.Succeeded);
        using var options = JsonDocument.Parse(result.OptionsJson!);
        var selection = options.RootElement.GetProperty("authenticatorSelection");
        Assert.Equal("required", selection.GetProperty("residentKey").GetString());
        Assert.Equal("required", selection.GetProperty("userVerification").GetString());
    }

    [Theory]
    [InlineData(null, false, true, "The passkey response was not valid.")]
    [InlineData("AQ", false, true, "The passkey was not recognised.")]
    [InlineData("AQ", true, false, "The LMS account is disabled.")]
    [InlineData("AQ", true, true, "The passkey could not be verified.")]
    public async Task Account_selection_still_rejects_invalid_or_unavailable_credentials(
        string? userHandle, bool knownCredential, bool enabled, string expectedError)
    {
        var user = User() with { IsEnabled = enabled };
        var now = DateTimeOffset.UtcNow;
        var credential = new EdgeGatewayPasskeyCredential(Guid.NewGuid(), user.Id, "AQ", "AQ", "AQ",
            0, "Test key", false, now, now, null);
        var service = CreateService(EdgeGatewaySecurityConfiguration.Empty with
        {
            Users = [user],
            Passkeys = knownCredential ? [credential] : []
        });
        var options = await service.BuildLoginOptionsAsync(Request(), default);
        var response = JsonSerializer.Serialize(new
        {
            id = "AQ", rawId = "AQ", type = "public-key",
            response = new { authenticatorData = "AQ", clientDataJSON = "AQ", signature = "AQ", userHandle }
        });
        var result = await service.CompleteLoginAsync(options.StateId!, response, Request(), default);

        Assert.False(result.Succeeded);
        Assert.Null(result.User);
        Assert.Equal(expectedError, result.ErrorMessage);
    }

    private static EdgeGatewaySecurityUser User() => new(Guid.NewGuid(), "person@example.com", "Person",
        true, 60, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);

    private static HttpRequest Request()
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("gateway.example.com");
        return request;
    }

    private static PasskeyAuthenticationService CreateService(EdgeGatewaySecurityConfiguration configuration) =>
        new(new SecurityStore(configuration), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PasskeyAuthenticationService>.Instance);

    private sealed class SecurityStore(EdgeGatewaySecurityConfiguration configuration) : IEdgeGatewaySecurityStore
    {
        public Task<EdgeGatewaySecurityConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);

        public Task SaveAsync(EdgeGatewaySecurityConfiguration value, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Rejected assertions must not update the security store.");
    }
}
