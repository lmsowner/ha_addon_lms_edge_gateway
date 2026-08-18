using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using LMS.EdgeGateway.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace HA.LMS.EdgeGateway.Services;

public sealed class PasskeyAuthenticationService(
    IEdgeGatewaySecurityStore securityStore,
    IMemoryCache memoryCache,
    ILogger<PasskeyAuthenticationService> logger)
{
    private const string RegistrationStatePrefix = "passkeys:registration:";
    private const string AssertionStatePrefix = "passkeys:assertion:";
    private static readonly JsonSerializerOptions PasskeyDeserializeOptions = BuildPasskeyDeserializeOptions();

    public async Task<IReadOnlyList<EdgeGatewayPasskeyCredential>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        return configuration.Passkeys
            .Where(passkey => passkey.UserId == userId)
            .OrderBy(passkey => passkey.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PasskeyOperationResult> DeleteAsync(Guid passkeyId, CancellationToken cancellationToken)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var passkeys = configuration.Passkeys.Where(passkey => passkey.Id != passkeyId).ToArray();
        if (passkeys.Length == configuration.Passkeys.Count)
        {
            return new PasskeyOperationResult(false, "The passkey was not found.");
        }

        await securityStore.SaveAsync(configuration with
        {
            Passkeys = passkeys,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
        return new PasskeyOperationResult(true, "Passkey removed.");
    }

    public async Task<PasskeyOptionsResult> BuildRegistrationOptionsAsync(
        Guid userId,
        string friendlyName,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var user = configuration.Users.FirstOrDefault(candidate => candidate.Id == userId);
        if (user is null || !user.IsEnabled)
        {
            return PasskeyOptionsResult.Fail("The selected LMS account is not available.");
        }

        var fido = BuildFido(request);
        var existingPasskeys = configuration.Passkeys.Where(passkey => passkey.UserId == user.Id).ToArray();
        var fidoUser = new Fido2User
        {
            Id = user.Id.ToByteArray(),
            Name = user.Email,
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName
        };

        var credentialOptions = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = existingPasskeys
                .Select(passkey => new PublicKeyCredentialDescriptor(PasskeyBase64Url.Decode(passkey.CredentialId)))
                .ToArray(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        var stateId = Guid.NewGuid().ToString("N");
        var displayName = string.IsNullOrWhiteSpace(friendlyName)
            ? "Passkey"
            : friendlyName.Trim();

        memoryCache.Set(
            $"{RegistrationStatePrefix}{stateId}",
            new PasskeyRegistrationState(user.Id, displayName, credentialOptions),
            TimeSpan.FromMinutes(5));

        return new PasskeyOptionsResult(true, null, stateId, SerializeCredentialOptions(credentialOptions));
    }

    public async Task<PasskeyOperationResult> CompleteRegistrationAsync(
        string stateId,
        string credentialJson,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!memoryCache.TryGetValue<PasskeyRegistrationState>(
                $"{RegistrationStatePrefix}{stateId}",
                out var state) ||
            state is null)
        {
            return new PasskeyOperationResult(false, "The passkey setup request has expired.");
        }

        var configuration = await securityStore.LoadAsync(cancellationToken);
        var targetUser = configuration.Users.FirstOrDefault(user => user.Id == state.UserId);
        if (targetUser is null || !targetUser.IsEnabled)
        {
            return new PasskeyOperationResult(false, "The selected LMS account is not available.");
        }

        var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
            credentialJson,
            PasskeyDeserializeOptions);
        if (attestationResponse is null)
        {
            return new PasskeyOperationResult(false, "The passkey response was not valid.");
        }

        try
        {
            var fido = BuildFido(request);
            var result = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = state.Options,
                IsCredentialIdUniqueToUserCallback = (credentialIdUserParams, _) =>
                {
                    var credentialId = PasskeyBase64Url.Encode(credentialIdUserParams.CredentialId);
                    var isUnique = configuration.Passkeys.All(passkey =>
                        !passkey.CredentialId.Equals(credentialId, StringComparison.Ordinal));
                    return Task.FromResult(isUnique);
                }
            }, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var passkey = new EdgeGatewayPasskeyCredential(
                Guid.NewGuid(),
                targetUser.Id,
                PasskeyBase64Url.Encode(result.Id),
                PasskeyBase64Url.Encode(result.PublicKey),
                PasskeyBase64Url.Encode(result.User.Id),
                result.SignCount,
                state.FriendlyName,
                result.IsBackedUp,
                now,
                now,
                null);

            await securityStore.SaveAsync(configuration with
            {
                Passkeys = [.. configuration.Passkeys, passkey],
                UpdatedAtUtc = now
            }, cancellationToken);

            memoryCache.Remove($"{RegistrationStatePrefix}{stateId}");
            return new PasskeyOperationResult(true, "Passkey added.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Passkey registration failed for add-on LMS user {UserId}.", targetUser.Id);
            return new PasskeyOperationResult(false, "The passkey could not be verified.");
        }
    }

    public async Task<PasskeyOptionsResult> BuildLoginOptionsAsync(
        string email,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var configuration = await securityStore.LoadAsync(cancellationToken);
        var user = configuration.Users.FirstOrDefault(candidate =>
            candidate.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
        if (user is null || !user.IsEnabled)
        {
            return PasskeyOptionsResult.Fail("No passkey is available for this email.");
        }

        var passkeys = configuration.Passkeys.Where(passkey => passkey.UserId == user.Id).ToArray();
        if (passkeys.Length == 0)
        {
            return PasskeyOptionsResult.Fail("No passkey is available for this email.");
        }

        var fido = BuildFido(request);
        var assertionOptions = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = passkeys
                .Select(passkey => new PublicKeyCredentialDescriptor(PasskeyBase64Url.Decode(passkey.CredentialId)))
                .ToArray(),
            UserVerification = UserVerificationRequirement.Required
        });

        var stateId = Guid.NewGuid().ToString("N");
        memoryCache.Set(
            $"{AssertionStatePrefix}{stateId}",
            new PasskeyAssertionState(user.Id, assertionOptions),
            TimeSpan.FromMinutes(5));

        return new PasskeyOptionsResult(true, null, stateId, SerializeAssertionOptions(assertionOptions));
    }

    public async Task<PasskeyLoginResult> CompleteLoginAsync(
        string stateId,
        string credentialJson,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!memoryCache.TryGetValue<PasskeyAssertionState>(
                $"{AssertionStatePrefix}{stateId}",
                out var state) ||
            state is null)
        {
            return PasskeyLoginResult.Fail("The passkey sign-in request has expired.");
        }

        var assertionResponse = JsonSerializer.Deserialize(
            credentialJson,
            FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse);
        if (assertionResponse is null || string.IsNullOrWhiteSpace(assertionResponse.Id))
        {
            return PasskeyLoginResult.Fail("The passkey response was not valid.");
        }

        var configuration = await securityStore.LoadAsync(cancellationToken);
        var credential = configuration.Passkeys.FirstOrDefault(passkey =>
            passkey.CredentialId.Equals(assertionResponse.Id, StringComparison.Ordinal));
        if (credential is null || credential.UserId != state.UserId)
        {
            return PasskeyLoginResult.Fail("The passkey was not recognised.");
        }

        var user = configuration.Users.FirstOrDefault(candidate => candidate.Id == credential.UserId);
        if (user is null || !user.IsEnabled)
        {
            return PasskeyLoginResult.Fail("The LMS account is disabled.");
        }

        try
        {
            var fido = BuildFido(request);
            var result = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = state.Options,
                StoredPublicKey = PasskeyBase64Url.Decode(credential.PublicKey),
                StoredSignatureCounter = credential.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (credentialIdUserHandleParams, _) =>
                {
                    var credentialId = PasskeyBase64Url.Encode(credentialIdUserHandleParams.CredentialId);
                    var userHandle = PasskeyBase64Url.Encode(credentialIdUserHandleParams.UserHandle);
                    return Task.FromResult(credential.CredentialId == credentialId && credential.UserHandle == userHandle);
                }
            }, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var passkeys = configuration.Passkeys.ToList();
            var passkeyIndex = passkeys.FindIndex(passkey => passkey.Id == credential.Id);
            passkeys[passkeyIndex] = credential with
            {
                SignatureCounter = result.SignCount,
                IsBackedUp = result.IsBackedUp,
                UpdatedAtUtc = now,
                LastUsedAtUtc = now
            };

            var users = configuration.Users.ToList();
            var userIndex = users.FindIndex(candidate => candidate.Id == user.Id);
            users[userIndex] = user with
            {
                LastLoginAtUtc = now,
                UpdatedAtUtc = now
            };

            await securityStore.SaveAsync(configuration with
            {
                Passkeys = passkeys,
                Users = users,
                UpdatedAtUtc = now
            }, cancellationToken);

            memoryCache.Remove($"{AssertionStatePrefix}{stateId}");
            return PasskeyLoginResult.Success(users[userIndex]);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Passkey sign-in failed for add-on LMS user {UserId}.", user.Id);
            return PasskeyLoginResult.Fail("The passkey could not be verified.");
        }
    }

    private static Fido2 BuildFido(HttpRequest request)
    {
        var publicOrigin = ResolvePublicOrigin(request);
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = publicOrigin.RelyingPartyId,
            ServerName = "Linux Made Sane Edge Gateway",
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { publicOrigin.Origin }
        });
    }

    private static PasskeyPublicOrigin ResolvePublicOrigin(HttpRequest request)
    {
        var host = ResolveForwardedHost(request);
        var scheme = ResolveForwardedScheme(request);
        var originHeader = FirstHeaderValue(request.Headers.Origin.ToString());

        if (Uri.TryCreate(originHeader, UriKind.Absolute, out var originUri) &&
            IsHttpScheme(originUri.Scheme) &&
            HostMatches(originUri, host))
        {
            scheme = originUri.Scheme;
            host = HostString.FromUriComponent(originUri.Authority);
        }

        if (!IsHttpScheme(scheme))
        {
            scheme = request.Scheme;
        }

        if (!IsHttpScheme(scheme))
        {
            scheme = Uri.UriSchemeHttps;
        }

        var hostValue = host.HasValue ? host.Value : request.Host.Value;
        var relyingPartyId = host.Host;
        if (string.IsNullOrWhiteSpace(relyingPartyId))
        {
            relyingPartyId = request.Host.Host;
        }

        return new PasskeyPublicOrigin(
            $"{scheme.ToLowerInvariant()}://{hostValue}",
            relyingPartyId);
    }

    private static HostString ResolveForwardedHost(HttpRequest request)
    {
        var forwardedHost = FirstHeaderValue(request.Headers["X-Forwarded-Host"].ToString());
        var host = ParseHost(forwardedHost);
        if (host.HasValue)
        {
            return host;
        }

        var forwardedHeaderHost = ParseForwardedHeaderValue(request.Headers["Forwarded"].ToString(), "host");
        host = ParseHost(forwardedHeaderHost);
        return host.HasValue ? host : request.Host;
    }

    private static string ResolveForwardedScheme(HttpRequest request)
    {
        var forwardedProto = FirstHeaderValue(request.Headers["X-Forwarded-Proto"].ToString());
        if (IsHttpScheme(forwardedProto))
        {
            return forwardedProto;
        }

        var forwardedScheme = FirstHeaderValue(request.Headers["X-Forwarded-Scheme"].ToString());
        if (IsHttpScheme(forwardedScheme))
        {
            return forwardedScheme;
        }

        var forwardedHeaderProto = ParseForwardedHeaderValue(request.Headers["Forwarded"].ToString(), "proto");
        return IsHttpScheme(forwardedHeaderProto) ? forwardedHeaderProto : request.Scheme;
    }

    private static HostString ParseHost(string? value)
    {
        var normalized = FirstHeaderValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return default;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            normalized = uri.Authority;
        }

        try
        {
            return HostString.FromUriComponent(normalized);
        }
        catch (FormatException)
        {
            return default;
        }
    }

    private static string ParseForwardedHeaderValue(string forwardedHeader, string key)
    {
        var firstForwarded = FirstHeaderValue(forwardedHeader);
        if (string.IsNullOrWhiteSpace(firstForwarded))
        {
            return string.Empty;
        }

        foreach (var segment in firstForwarded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return parts[1].Trim('"');
            }
        }

        return string.Empty;
    }

    private static string FirstHeaderValue(string? value) =>
        (value ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? string.Empty;

    private static bool HostMatches(Uri originUri, HostString host) =>
        originUri.Host.Equals(host.Host, StringComparison.OrdinalIgnoreCase) &&
        (originUri.IsDefaultPort || !host.Port.HasValue || originUri.Port == host.Port.Value);

    private static bool IsHttpScheme(string? scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static string SerializeCredentialOptions(CredentialCreateOptions credentialOptions) =>
        JsonSerializer.Serialize(credentialOptions, FidoModelSerializerContext.Default.CredentialCreateOptions);

    private static string SerializeAssertionOptions(AssertionOptions assertionOptions) =>
        JsonSerializer.Serialize(assertionOptions, FidoModelSerializerContext.Default.AssertionOptions);

    private static JsonSerializerOptions BuildPasskeyDeserializeOptions() =>
        new(FidoModelSerializerContext.Default.Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                FidoModelSerializerContext.Default,
                new DefaultJsonTypeInfoResolver())
        };

    private sealed record PasskeyRegistrationState(
        Guid UserId,
        string FriendlyName,
        CredentialCreateOptions Options);

    private sealed record PasskeyAssertionState(Guid UserId, AssertionOptions Options);

    private sealed record PasskeyPublicOrigin(string Origin, string RelyingPartyId);
}

public sealed record PasskeyOperationResult(bool Succeeded, string Message);

public sealed record PasskeyOptionsResult(
    bool Succeeded,
    string? ErrorMessage,
    string? StateId,
    string? OptionsJson)
{
    public static PasskeyOptionsResult Fail(string errorMessage) => new(false, errorMessage, null, null);
}

public sealed record PasskeyLoginResult(
    bool Succeeded,
    string? ErrorMessage,
    EdgeGatewaySecurityUser? User)
{
    public static PasskeyLoginResult Success(EdgeGatewaySecurityUser user) => new(true, null, user);
    public static PasskeyLoginResult Fail(string errorMessage) => new(false, errorMessage, null);
}
