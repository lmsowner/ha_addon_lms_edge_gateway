using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

const string ProductName = "LMS Tesla Fleet Helper";
const string TeslaPublicKeyPath = "/.well-known/appspecific/com.tesla.3p.public-key.pem";
const string TeslaPublicKeyContentType = "application/x-pem-file";
const string TeslaAuthorizeEndpoint = "https://auth.tesla.com/oauth2/v3/authorize";
const string DefaultFleetApiAudience = "https://fleet-api.prd.na.vn.cloud.tesla.com";
const string DefaultTeslaScopes = "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://0.0.0.0:5055");
builder.Services.AddSingleton<TeslaFleetStore>();
builder.Services.AddHttpClient<EdgeGatewayPublicAssetClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});
builder.Services.AddHttpClient<TeslaFleetOAuthClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});
builder.Services.AddHttpClient<TeslaFleetPartnerClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});
builder.Services.AddHttpClient<TeslaFleetApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", product = ProductName }));

app.MapGet("/", async (TeslaFleetStore store) =>
{
    var state = await store.LoadAsync();
    return Results.Content(RenderPage(state), "text/html; charset=utf-8");
});

app.MapPost("/actions/save-settings", async (
    HttpContext context,
    TeslaFleetStore store) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var state = await store.LoadAsync();
    try
    {
        state = state with
        {
            EdgeGatewayUrl = NormalizeHttpUrl(form["edge_gateway_url"].ToString()),
            OriginDomain = NormalizeDomain(form["origin_domain"].ToString(), required: false),
            PublicUpstreamUrl = NormalizeHttpUrl(form["public_upstream_url"].ToString(), "http://127.0.0.1:5055"),
            TeslaClientId = form["tesla_client_id"].ToString().Trim(),
            TeslaClientSecret = string.IsNullOrWhiteSpace(form["tesla_client_secret"].ToString())
                ? state.TeslaClientSecret
                : form["tesla_client_secret"].ToString().Trim(),
            FleetApiAudience = NormalizeHttpUrl(form["fleet_api_audience"].ToString(), DefaultFleetApiAudience),
            TeslaScopes = NormalizeScopes(form["tesla_scopes"].ToString()),
            LastStatus = "Settings saved",
            LastMessage = "Tesla Fleet Helper settings were saved.",
            LastChecks = []
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Settings error",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/generate-key", async (
    HttpContext context,
    TeslaFleetStore store) =>
{
    var state = await store.LoadAsync();
    try
    {
        state = await GenerateKeyAsync(store, state, context.RequestAborted);
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Key generation failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
        await store.SaveAsync(state, context.RequestAborted);
    }

    return Results.Redirect("/");
});

app.MapPost("/actions/publish", async (
    HttpContext context,
    TeslaFleetStore store,
    EdgeGatewayPublicAssetClient edgeGatewayClient,
    TeslaFleetPartnerClient partnerClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        if (string.IsNullOrWhiteSpace(state.PublicKeyPem) ||
            string.IsNullOrWhiteSpace(state.PrivateKeyPath) ||
            !File.Exists(state.PrivateKeyPath))
        {
            state = await GenerateKeyAsync(store, state, context.RequestAborted);
        }

        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        var result = await edgeGatewayClient.PublishAsync(
            state.EdgeGatewayUrl,
            originDomain,
            state.PublicKeyPem,
            context.RequestAborted);
        var routeResult = result.Succeeded
            ? await edgeGatewayClient.PublishOAuthRouteAsync(
                state.EdgeGatewayUrl,
                originDomain,
                state.PublicUpstreamUrl,
                context.RequestAborted)
            : PublicProxyRoutePublishResponse.Failure("OAuth route was not published because the public key publish failed.");
        var warnings = result.Warnings
            .Concat(routeResult.Warnings)
            .ToList();
        var partnerResult = result.Succeeded && routeResult.Succeeded
            ? await TryRegisterPartnerAccountAsync(state, originDomain, partnerClient, context.RequestAborted)
            : TeslaPartnerRegistrationResult.Failure("Tesla Partner Account registration was skipped because publishing failed.");
        warnings.Add(partnerResult.Summary);
        warnings.AddRange(partnerResult.Checks);

        var publishSucceeded = result.Succeeded && routeResult.Succeeded && partnerResult.Succeeded;
        state = state with
        {
            OriginDomain = originDomain,
            PublicAssetId = result.Asset?.Id ?? state.PublicAssetId,
            PublicOAuthRouteId = routeResult.Route?.Id ?? state.PublicOAuthRouteId,
            PublicKeyUrl = result.Asset?.PublicUrl ?? BuildPublicKeyUrl(originDomain),
            OAuthStartUrl = BuildOAuthStartUrl(originDomain),
            OAuthRedirectUri = BuildOAuthRedirectUri(originDomain),
            FleetApiAudience = partnerResult.Succeeded ? partnerResult.Audience : state.FleetApiAudience,
            LastPartnerRegistrationUtc = partnerResult.Succeeded ? DateTimeOffset.UtcNow : state.LastPartnerRegistrationUtc,
            PartnerRegistrationAudience = partnerResult.Succeeded ? partnerResult.Audience : state.PartnerRegistrationAudience,
            PartnerRegistrationStatus = partnerResult.Succeeded ? "Registered" : "Registration failed",
            PartnerRegistrationMessage = partnerResult.Summary,
            LastPublishedUtc = publishSucceeded ? DateTimeOffset.UtcNow : state.LastPublishedUtc,
            LastStatus = publishSucceeded ? "Published + registered" : "Publish failed",
            LastMessage = $"{result.Summary} {routeResult.Summary} {partnerResult.Summary}".Trim(),
            LastChecks = warnings
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Publish failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/verify", async (
    HttpContext context,
    TeslaFleetStore store,
    EdgeGatewayPublicAssetClient edgeGatewayClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var result = state.PublicAssetId.HasValue
            ? await edgeGatewayClient.VerifyAsync(state.EdgeGatewayUrl, state.PublicAssetId.Value, context.RequestAborted)
            : await edgeGatewayClient.VerifyPublicUrlAsync(state.PublicKeyUrl, state.PublicKeyPem, context.RequestAborted);
        state = state with
        {
            LastVerifiedUtc = DateTimeOffset.UtcNow,
            LastStatus = result.Succeeded ? "Verified" : "Verification failed",
            LastMessage = result.Message,
            LastChecks = result.Checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Verification failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/register-partner", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetPartnerClient partnerClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        var result = await TryRegisterPartnerAccountAsync(state, originDomain, partnerClient, context.RequestAborted);
        state = state with
        {
            OriginDomain = originDomain,
            FleetApiAudience = result.Succeeded ? result.Audience : state.FleetApiAudience,
            LastPartnerRegistrationUtc = result.Succeeded ? DateTimeOffset.UtcNow : state.LastPartnerRegistrationUtc,
            PartnerRegistrationAudience = result.Succeeded ? result.Audience : state.PartnerRegistrationAudience,
            PartnerRegistrationStatus = result.Succeeded ? "Registered" : "Registration failed",
            PartnerRegistrationMessage = result.Summary,
            LastStatus = result.Succeeded ? "Tesla registered" : "Tesla registration failed",
            LastMessage = result.Summary,
            LastChecks = result.Checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Tesla registration failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/test-public-key", async (
    HttpContext context,
    TeslaFleetStore store,
    EdgeGatewayPublicAssetClient edgeGatewayClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        var publicKeyUrl = string.IsNullOrWhiteSpace(state.PublicKeyUrl)
            ? BuildPublicKeyUrl(originDomain)
            : state.PublicKeyUrl;
        var result = await edgeGatewayClient.VerifyPublicUrlAsync(
            publicKeyUrl,
            state.PublicKeyPem,
            context.RequestAborted);
        state = state with
        {
            OriginDomain = originDomain,
            PublicKeyUrl = publicKeyUrl,
            LastVerifiedUtc = DateTimeOffset.UtcNow,
            LastStatus = result.Succeeded ? "Public key reachable" : "Public key check failed",
            LastMessage = result.Message,
            LastChecks = result.Checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Public key check failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/test-tesla-public-key", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetPartnerClient partnerClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        var expectedPublicKeyHex = ExportUncompressedPublicKeyHex(state.PublicKeyPem);
        var audience = ResolveFleetApiAudience(state.FleetApiAudience, state.AccessToken);
        var result = await partnerClient.GetRegisteredPublicKeyAsync(
            state.TeslaClientId,
            state.TeslaClientSecret,
            originDomain,
            audience,
            context.RequestAborted);
        var matches = result.Succeeded &&
                      result.PublicKeyHex.Equals(expectedPublicKeyHex, StringComparison.OrdinalIgnoreCase);
        var checks = result.Checks.ToList();
        checks.Add(matches
            ? "Tesla returned the same public key as the local helper key."
            : "Tesla did not return the same public key as the local helper key.");
        state = state with
        {
            OriginDomain = originDomain,
            FleetApiAudience = result.Succeeded ? result.Audience : state.FleetApiAudience,
            LastPartnerRegistrationUtc = result.Succeeded ? DateTimeOffset.UtcNow : state.LastPartnerRegistrationUtc,
            PartnerRegistrationAudience = result.Succeeded ? result.Audience : state.PartnerRegistrationAudience,
            PartnerRegistrationStatus = matches ? "Registered" : "Registration mismatch",
            PartnerRegistrationMessage = matches ? "Tesla Partner Account public key matches this helper." : result.Summary,
            LastStatus = matches ? "Tesla key matched" : "Tesla key mismatch",
            LastMessage = matches ? "Tesla can retrieve the registered public key for this origin domain." : result.Summary,
            LastChecks = checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Tesla key lookup failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/refresh-token", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetOAuthClient oauthClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var refreshed = await RefreshAccessTokenAsync(state, oauthClient, context.RequestAborted);
        state = refreshed.State with
        {
            LastStatus = "Token refreshed",
            LastMessage = "Tesla OAuth access token was refreshed and saved locally.",
            LastChecks = refreshed.Checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Token refresh failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapPost("/actions/list-vehicles", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetOAuthClient oauthClient,
    TeslaFleetApiClient fleetApiClient) =>
{
    var state = await store.LoadAsync();
    try
    {
        var token = await EnsureUsableAccessTokenAsync(state, oauthClient, context.RequestAborted);
        var result = await fleetApiClient.GetVehiclesAsync(
            token.State.FleetApiAudience,
            token.State.AccessToken,
            context.RequestAborted);
        var checks = token.Checks.Concat(result.Checks).ToList();
        state = token.State with
        {
            LastVehicleDiagnosticsUtc = DateTimeOffset.UtcNow,
            LastVehicleDiagnosticsSummary = result.Summary,
            LastStatus = result.Succeeded ? "Tesla API reachable" : "Tesla API check failed",
            LastMessage = result.Summary,
            LastChecks = checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Tesla API check failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return Results.Redirect("/");
});

app.MapGet("/tesla_fleet.key", async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(state.PrivateKeyPath) || !File.Exists(state.PrivateKeyPath))
    {
        return Results.NotFound("Generate a Tesla Fleet key before exporting tesla_fleet.key.");
    }

    return Results.File(
        state.PrivateKeyPath,
        TeslaPublicKeyContentType,
        "tesla_fleet.key");
});

app.MapGet(TeslaPublicKeyPath, async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    return string.IsNullOrWhiteSpace(state.PublicKeyPem)
        ? Results.NotFound("Generate a Tesla Fleet key first.")
        : Results.Bytes(Encoding.UTF8.GetBytes(state.PublicKeyPem), TeslaPublicKeyContentType);
});

app.MapGet(TeslaFleetDefaults.OAuthStartPath, async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    try
    {
        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        if (string.IsNullOrWhiteSpace(state.TeslaClientId))
        {
            throw new InvalidOperationException("Enter the Tesla Developer client ID before starting OAuth.");
        }

        var oauthState = CreateUrlSafeToken();
        var nonce = CreateUrlSafeToken();
        var updated = state with
        {
            OriginDomain = originDomain,
            OAuthState = oauthState,
            OAuthNonce = nonce,
            OAuthStartedUtc = DateTimeOffset.UtcNow,
            OAuthRedirectUri = BuildOAuthRedirectUri(originDomain),
            OAuthStartUrl = BuildOAuthStartUrl(originDomain),
            LastStatus = "OAuth started",
            LastMessage = "Redirecting to Tesla authorization.",
            LastChecks = []
        };
        await store.SaveAsync(updated, cancellationToken);
        return Results.Redirect(BuildTeslaAuthorizeUrl(updated, oauthState, nonce));
    }
    catch (Exception exception)
    {
        var failed = state with
        {
            LastStatus = "OAuth start failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
        await store.SaveAsync(failed, cancellationToken);
        return Results.Content(RenderSimplePage("OAuth start failed", exception.Message), "text/html; charset=utf-8");
    }
});

app.MapGet(TeslaFleetDefaults.OAuthCallbackPath, HandleOAuthCallbackAsync);
app.MapGet(TeslaFleetDefaults.OAuthCallbackAliasPath, HandleOAuthCallbackAsync);

app.Run();

static async Task<IResult> HandleOAuthCallbackAsync(
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetOAuthClient oauthClient)
{
    var state = await store.LoadAsync(context.RequestAborted);
    try
    {
        var error = context.Request.Query["error"].ToString();
        if (!string.IsNullOrWhiteSpace(error))
        {
            var errorDescription = context.Request.Query["error_description"].ToString();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorDescription)
                ? $"Tesla returned OAuth error {error}."
                : $"Tesla returned OAuth error {error}: {errorDescription}");
        }

        var code = context.Request.Query["code"].ToString();
        var returnedState = context.Request.Query["state"].ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Tesla callback did not include an authorization code.");
        }

        if (string.IsNullOrWhiteSpace(returnedState) ||
            string.IsNullOrWhiteSpace(state.OAuthState) ||
            !returnedState.Equals(state.OAuthState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Tesla OAuth state did not match. Start authorization again.");
        }

        if (!state.OAuthStartedUtc.HasValue ||
            DateTimeOffset.UtcNow - state.OAuthStartedUtc.Value > TimeSpan.FromMinutes(20))
        {
            throw new InvalidOperationException("Tesla OAuth state expired. Start authorization again.");
        }

        if (string.IsNullOrWhiteSpace(state.TeslaClientId) ||
            string.IsNullOrWhiteSpace(state.TeslaClientSecret))
        {
            throw new InvalidOperationException("Tesla client ID and client secret are required before exchanging the authorization code.");
        }

        var originDomain = NormalizeDomain(state.OriginDomain, required: true);
        var token = await oauthClient.ExchangeAuthorizationCodeAsync(
            state.TeslaClientId,
            state.TeslaClientSecret,
            code,
            BuildOAuthRedirectUri(originDomain),
            state.FleetApiAudience,
            state.TeslaScopes,
            context.RequestAborted);
        var expiresUtc = token.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
            : (DateTimeOffset?)null;
        var fleetApiAudience = ResolveFleetApiAudience(state.FleetApiAudience, token.AccessToken);
        var updated = state with
        {
            OAuthState = string.Empty,
            OAuthNonce = string.Empty,
            OAuthStartedUtc = null,
            FleetApiAudience = fleetApiAudience,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            TokenType = token.TokenType,
            TokenExpiresUtc = expiresUtc,
            LastOAuthUtc = DateTimeOffset.UtcNow,
            LastStatus = "OAuth connected",
            LastMessage = "Tesla OAuth completed and tokens were saved locally in the Tesla Fleet Helper add-on.",
            LastChecks =
            [
                $"Token type: {FirstNonEmpty(token.TokenType, "unknown")}.",
                $"Fleet API base URL: {fleetApiAudience}.",
                expiresUtc.HasValue ? $"Access token expires at {expiresUtc.Value:O}." : "Tesla did not return an access token expiry.",
                string.IsNullOrWhiteSpace(token.RefreshToken) ? "No refresh token was returned." : "Refresh token saved locally."
            ]
        };
        await store.SaveAsync(updated, context.RequestAborted);
        return Results.Content(RenderOAuthCompletePage(updated), "text/html; charset=utf-8");
    }
    catch (Exception exception)
    {
        var failed = state with
        {
            OAuthState = string.Empty,
            OAuthNonce = string.Empty,
            OAuthStartedUtc = null,
            LastStatus = "OAuth failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
        await store.SaveAsync(failed, context.RequestAborted);
        return Results.Content(RenderSimplePage("OAuth failed", exception.Message), "text/html; charset=utf-8");
    }
}

static async Task<TeslaFleetState> GenerateKeyAsync(
    TeslaFleetStore store,
    TeslaFleetState state,
    CancellationToken cancellationToken)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privateKeyPem = key.ExportECPrivateKeyPem();
    var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
    var privateKeyPath = store.PrivateKeyPath;
    Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
    await File.WriteAllTextAsync(privateKeyPath, privateKeyPem, cancellationToken);
    TrySetOwnerOnly(privateKeyPath);

    var updated = state with
    {
        PrivateKeyPath = privateKeyPath,
        PublicKeyPem = publicKeyPem,
        KeyGeneratedUtc = DateTimeOffset.UtcNow,
        LastStatus = "Key ready",
        LastMessage = "Generated a new EC P-256 key pair. Publish the public key through Edge Gateway before using it with Tesla.",
        LastChecks = []
    };
    await store.SaveAsync(updated, cancellationToken);
    return updated;
}

static async Task<TeslaPartnerRegistrationResult> TryRegisterPartnerAccountAsync(
    TeslaFleetState state,
    string originDomain,
    TeslaFleetPartnerClient partnerClient,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(state.TeslaClientId) ||
        string.IsNullOrWhiteSpace(state.TeslaClientSecret))
    {
        return TeslaPartnerRegistrationResult.Failure("Tesla Partner Account registration skipped: enter the Tesla client ID and client secret.");
    }

    var audience = ResolveFleetApiAudience(state.FleetApiAudience, state.AccessToken);
    return await partnerClient.RegisterPartnerAccountAsync(
        state.TeslaClientId,
        state.TeslaClientSecret,
        originDomain,
        audience,
        cancellationToken);
}

static async Task<TeslaAccessTokenResult> EnsureUsableAccessTokenAsync(
    TeslaFleetState state,
    TeslaFleetOAuthClient oauthClient,
    CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(state.AccessToken) &&
        state.TokenExpiresUtc.HasValue &&
        state.TokenExpiresUtc.Value > DateTimeOffset.UtcNow.AddMinutes(5))
    {
        return new TeslaAccessTokenResult(
            state,
            false,
            [$"Access token is valid until {state.TokenExpiresUtc.Value:O}."]);
    }

    return await RefreshAccessTokenAsync(state, oauthClient, cancellationToken);
}

static async Task<TeslaAccessTokenResult> RefreshAccessTokenAsync(
    TeslaFleetState state,
    TeslaFleetOAuthClient oauthClient,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(state.RefreshToken))
    {
        throw new InvalidOperationException("Complete Tesla OAuth before refreshing the access token.");
    }

    if (string.IsNullOrWhiteSpace(state.TeslaClientId) ||
        string.IsNullOrWhiteSpace(state.TeslaClientSecret))
    {
        throw new InvalidOperationException("Tesla client ID and client secret are required to refresh the OAuth token.");
    }

    var token = await oauthClient.RefreshAccessTokenAsync(
        state.TeslaClientId,
        state.TeslaClientSecret,
        state.RefreshToken,
        state.FleetApiAudience,
        state.TeslaScopes,
        cancellationToken);
    var expiresUtc = token.ExpiresIn > 0
        ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
        : (DateTimeOffset?)null;
    var fleetApiAudience = ResolveFleetApiAudience(state.FleetApiAudience, token.AccessToken);
    var updated = state with
    {
        FleetApiAudience = fleetApiAudience,
        AccessToken = token.AccessToken,
        RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? state.RefreshToken : token.RefreshToken,
        TokenType = token.TokenType,
        TokenExpiresUtc = expiresUtc,
        LastTokenRefreshUtc = DateTimeOffset.UtcNow
    };
    var checks = new List<string>
    {
        "Tesla refresh token grant completed.",
        $"Fleet API base URL: {fleetApiAudience}.",
        expiresUtc.HasValue ? $"Access token expires at {expiresUtc.Value:O}." : "Tesla did not return an access token expiry."
    };
    return new TeslaAccessTokenResult(updated, true, checks);
}

static string ExportUncompressedPublicKeyHex(string publicKeyPem)
{
    if (string.IsNullOrWhiteSpace(publicKeyPem))
    {
        throw new InvalidOperationException("Generate a Tesla Fleet key before checking Tesla registration.");
    }

    using var key = ECDsa.Create();
    key.ImportFromPem(publicKeyPem);
    var parameters = key.ExportParameters(false);
    if (parameters.Q.X is null || parameters.Q.Y is null)
    {
        throw new InvalidOperationException("The public key could not be exported as an EC P-256 point.");
    }

    var x = NormalizeEcCoordinate(parameters.Q.X, 32);
    var y = NormalizeEcCoordinate(parameters.Q.Y, 32);
    var point = new byte[1 + x.Length + y.Length];
    point[0] = 0x04;
    x.CopyTo(point.AsSpan(1));
    y.CopyTo(point.AsSpan(1 + x.Length));
    return Convert.ToHexString(point).ToLowerInvariant();
}

static byte[] NormalizeEcCoordinate(byte[] value, int size)
{
    if (value.Length == size)
    {
        return value;
    }

    var normalized = new byte[size];
    if (value.Length > size)
    {
        value.AsSpan(value.Length - size, size).CopyTo(normalized);
    }
    else
    {
        value.AsSpan().CopyTo(normalized.AsSpan(size - value.Length));
    }

    return normalized;
}

static void TrySetOwnerOnly(string path)
{
    try
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    catch
    {
        // Permission tightening is best effort inside Home Assistant add-on containers.
    }
}

static string RenderPage(TeslaFleetState state)
{
    var hasKey = !string.IsNullOrWhiteSpace(state.PublicKeyPem) &&
                 !string.IsNullOrWhiteSpace(state.PrivateKeyPath) &&
                 File.Exists(state.PrivateKeyPath);
    var originDomain = string.IsNullOrWhiteSpace(state.OriginDomain) ? "tesla.example.com" : state.OriginDomain;
    var publicKeyUrl = string.IsNullOrWhiteSpace(state.PublicKeyUrl)
        ? BuildPublicKeyUrl(originDomain)
        : state.PublicKeyUrl;
    var oauthRedirectUri = string.IsNullOrWhiteSpace(state.OAuthRedirectUri)
        ? BuildOAuthRedirectUri(originDomain)
        : state.OAuthRedirectUri;
    var oauthStartUrl = string.IsNullOrWhiteSpace(state.OAuthStartUrl)
        ? BuildOAuthStartUrl(originDomain)
        : state.OAuthStartUrl;
    var virtualKeyUrl = string.IsNullOrWhiteSpace(state.OriginDomain)
        ? "https://tesla.com/_ak/tesla.example.com"
        : $"https://tesla.com/_ak/{state.OriginDomain}";
    var hasOAuthToken = !string.IsNullOrWhiteSpace(state.RefreshToken) || !string.IsNullOrWhiteSpace(state.AccessToken);
    var isPartnerRegistered = state.PartnerRegistrationStatus.Equals("Registered", StringComparison.OrdinalIgnoreCase);
    var keyActionLabel = hasKey ? "Rotate key" : "Generate key";
    var publishActionLabel = isPartnerRegistered ? "Republish Edge Gateway assets" : "Publish + register";
    var keyRotationCallout = hasKey
        ? """
        <div class="callout warn" style="margin:12px 0">
          <strong>Key rotation warning</strong>
          <p style="margin:8px 0 0">Rotating the key changes the Tesla public key and normally requires republishing, re-registering the Partner Account, and reinstalling the virtual key.</p>
        </div>
        """
        : string.Empty;
    var manualRegisterButton = isPartnerRegistered
        ? string.Empty
        : """
          <form method="post" action="actions/register-partner"><button type="submit">Register domain with Tesla</button></form>
        """;
    var diagnosticActions = """
          <form method="post" action="actions/test-public-key"><button type="submit">Check public key URL</button></form>
          <form method="post" action="actions/test-tesla-public-key"><button type="submit">Check Tesla public key</button></form>
          <form method="post" action="actions/refresh-token"><button type="submit">Refresh Tesla token</button></form>
          <form method="post" action="actions/list-vehicles"><button type="submit">Check Tesla vehicles API</button></form>
        """;
    var tokenStatus = hasOAuthToken
        ? state.TokenExpiresUtc.HasValue
            ? $"Connected, access expires {state.TokenExpiresUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Connected"
        : "Not connected";
    var partnerStatus = isPartnerRegistered
        ? "Domain registered"
        : string.IsNullOrWhiteSpace(state.PartnerRegistrationStatus) ? "Not registered" : state.PartnerRegistrationStatus;
    var lastChecks = state.LastChecks ?? [];
    var checks = lastChecks.Count == 0
        ? "<li>No detailed checks yet.</li>"
        : string.Concat(lastChecks.Select(check => $"<li>{H(check)}</li>"));

    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{ProductName}}</title>
  <style>
    :root {
      color-scheme: dark light;
      --bg: #0f141b;
      --surface: #161d27;
      --surface-2: #1d2734;
      --border: #334155;
      --text: #eef2f7;
      --muted: #a8b3c2;
      --accent: #4fd1c5;
      --accent-2: #8bd5ff;
      --danger: #ff8a8a;
      --ready: #9be7b4;
      --warning: #ffd37d;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    main { width: min(1180px, calc(100vw - 32px)); margin: 0 auto; padding: 28px 0 48px; }
    header { display: flex; justify-content: space-between; gap: 24px; align-items: flex-start; margin-bottom: 18px; }
    h1, h2, h3, p { margin-top: 0; }
    h1 { font-size: 28px; margin-bottom: 8px; }
    h2 { font-size: 18px; margin-bottom: 10px; }
    h3 { font-size: 15px; margin-bottom: 8px; }
    .meta { color: var(--muted); line-height: 1.5; max-width: 820px; }
    .grid { display: grid; grid-template-columns: 1.1fr .9fr; gap: 16px; align-items: start; }
    .cards { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin: 16px 0; }
    .card {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 16px;
      box-shadow: 0 16px 36px rgba(0, 0, 0, .2);
    }
    .status {
      display: inline-flex;
      align-items: center;
      min-height: 28px;
      padding: 4px 10px;
      border: 1px solid var(--border);
      border-radius: 999px;
      color: var(--muted);
      font-size: 13px;
      white-space: nowrap;
    }
    .status.ready { color: var(--ready); border-color: rgba(155, 231, 180, .5); }
    .status.warn { color: var(--warning); border-color: rgba(255, 211, 125, .5); }
    .status.fail { color: var(--danger); border-color: rgba(255, 138, 138, .5); }
    label { display: grid; gap: 7px; margin-bottom: 12px; color: var(--muted); font-size: 13px; }
    input, textarea {
      width: 100%;
      min-height: 42px;
      border: 1px solid var(--border);
      border-radius: 7px;
      background: #0c1118;
      color: var(--text);
      padding: 10px 12px;
      font: inherit;
    }
    textarea { min-height: 220px; resize: vertical; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
    code, .value {
      display: block;
      overflow-wrap: anywhere;
      border: 1px solid var(--border);
      border-radius: 7px;
      background: #0c1118;
      padding: 10px 12px;
      color: var(--accent-2);
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      font-size: 13px;
    }
    .actions { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
    button, .button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: 40px;
      border: 1px solid var(--border);
      border-radius: 7px;
      padding: 9px 13px;
      background: var(--surface-2);
      color: var(--text);
      text-decoration: none;
      cursor: pointer;
      font: inherit;
      font-weight: 650;
    }
    button.primary, .button.primary { background: var(--accent); border-color: var(--accent); color: #061316; }
    button:disabled, .button.disabled { opacity: .55; cursor: not-allowed; }
    .fact-grid { display: grid; gap: 10px; }
    .fact-grid div span { display: block; color: var(--muted); font-size: 12px; margin-bottom: 5px; }
    .callout {
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 12px 14px;
      background: var(--surface-2);
      color: var(--muted);
      line-height: 1.45;
    }
    .callout strong { color: var(--text); }
    .callout.warn { border-color: rgba(255, 211, 125, .45); }
    ul { margin: 8px 0 0 18px; padding: 0; color: var(--muted); line-height: 1.55; }
    .split-actions { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
    @media (max-width: 900px) {
      main { width: min(100vw - 20px, 760px); padding-top: 18px; }
      header, .grid, .cards, .split-actions { grid-template-columns: 1fr; display: grid; }
      header { gap: 10px; }
    }
  </style>
</head>
<body>
  <main>
    <header>
      <div>
        <h1>{{ProductName}}</h1>
        <p class="meta">Generate Tesla Fleet keys, publish the public key through LMS Edge Gateway, run Tesla OAuth through this helper, and keep Tesla-specific bridge state out of Edge Gateway.</p>
      </div>
      <span class="status {{BuildStatusClass(state.LastStatus)}}">{{H(string.IsNullOrWhiteSpace(state.LastStatus) ? "Not configured" : state.LastStatus)}}</span>
    </header>

    <section class="cards">
      <div class="card"><h3>Key</h3><span class="status {{(hasKey ? "ready" : "warn")}}">{{(hasKey ? "EC P-256 ready" : "Generate required")}}</span></div>
      <div class="card"><h3>Publish</h3><span class="status {{(!string.IsNullOrWhiteSpace(state.PublicAssetId?.ToString()) ? "ready" : "warn")}}">{{(!string.IsNullOrWhiteSpace(state.PublicAssetId?.ToString()) ? "Edge Gateway asset linked" : "Not published")}}</span></div>
      <div class="card"><h3>Tesla</h3><span class="status {{(isPartnerRegistered ? "ready" : "warn")}}">{{H(partnerStatus)}}</span></div>
      <div class="card"><h3>OAuth</h3><span class="status {{(hasOAuthToken ? "ready" : "warn")}}">{{H(tokenStatus)}}</span></div>
    </section>

    <section class="grid">
      <div class="card">
        <h2>Setup</h2>
        <form method="post" action="actions/save-settings">
          <label>
            Edge Gateway API URL
            <input name="edge_gateway_url" value="{{H(state.EdgeGatewayUrl)}}" autocomplete="off" />
          </label>
          <label>
            Helper upstream URL for Edge Gateway
            <input name="public_upstream_url" value="{{H(state.PublicUpstreamUrl)}}" autocomplete="off" />
          </label>
          <label>
            Tesla origin domain
            <input name="origin_domain" value="{{H(state.OriginDomain)}}" placeholder="tesla.example.com" autocomplete="off" />
          </label>
          <label>
            Tesla client ID
            <input name="tesla_client_id" value="{{H(state.TeslaClientId)}}" autocomplete="off" />
          </label>
          <label>
            Tesla client secret
            <input name="tesla_client_secret" value="" placeholder="{{(string.IsNullOrWhiteSpace(state.TeslaClientSecret) ? "Not saved" : "Saved - leave blank to keep")}}" autocomplete="off" />
          </label>
          <label>
            Fleet API base URL
            <input name="fleet_api_audience" value="{{H(state.FleetApiAudience)}}" autocomplete="off" />
          </label>
          <label>
            Tesla OAuth scopes
            <input name="tesla_scopes" value="{{H(state.TeslaScopes)}}" autocomplete="off" />
          </label>
          <div class="actions">
            <button type="submit">Save settings</button>
          </div>
        </form>
        <hr style="border:0;border-top:1px solid var(--border);margin:16px 0" />
        {{keyRotationCallout}}
        <div class="split-actions">
          <form method="post" action="actions/generate-key"><button type="submit">{{H(keyActionLabel)}}</button></form>
          <form method="post" action="actions/publish"><button class="primary" type="submit">{{H(publishActionLabel)}}</button></form>
          <form method="post" action="actions/verify"><button type="submit">Verify public URL</button></form>
          {{manualRegisterButton}}
          <a class="button" href="{{H(oauthStartUrl)}}">Start Tesla OAuth</a>
          <a class="button {{(hasKey ? "" : "disabled")}}" href="tesla_fleet.key">Export tesla_fleet.key</a>
        </div>
      </div>

      <div class="card">
        <h2>Tesla Developer Values</h2>
        <div class="fact-grid">
          <div><span>Origin domain</span><code>{{H(originDomain)}}</code></div>
          <div><span>Public key URL</span><code>{{H(publicKeyUrl)}}</code></div>
          <div><span>Tesla OAuth start URL</span><code>{{H(oauthStartUrl)}}</code></div>
          <div><span>Tesla OAuth redirect URI</span><code>{{H(oauthRedirectUri)}}</code></div>
          <div><span>Fleet API base URL</span><code>{{H(state.FleetApiAudience)}}</code></div>
          <div><span>Virtual key install URL</span><code>{{H(virtualKeyUrl)}}</code></div>
        </div>
        <div class="callout" style="margin-top:12px">
          Register the Tesla OAuth redirect URI exactly as shown above in the Tesla Developer app for this client ID. Do not use the OAuth start URL or the Home Assistant redirect URL for this helper flow.
        </div>
      </div>
    </section>

    <section class="grid" style="margin-top:16px">
      <div class="card">
        <h2>Diagnostics</h2>
        <div class="callout">
          <strong>{{H(string.IsNullOrWhiteSpace(state.LastStatus) ? "No action yet" : state.LastStatus)}}</strong>
          <p style="margin:8px 0 0">{{H(string.IsNullOrWhiteSpace(state.LastMessage) ? "Save settings, generate a key, then publish through Edge Gateway." : state.LastMessage)}}</p>
          <ul>{{checks}}</ul>
        </div>
        <div class="fact-grid" style="margin-top:12px">
          <div><span>Key generated</span><code>{{FormatDate(state.KeyGeneratedUtc)}}</code></div>
          <div><span>Last published</span><code>{{FormatDate(state.LastPublishedUtc)}}</code></div>
          <div><span>Last verified</span><code>{{FormatDate(state.LastVerifiedUtc)}}</code></div>
          <div><span>Last Tesla registration</span><code>{{FormatDate(state.LastPartnerRegistrationUtc)}}</code></div>
          <div><span>Tesla registration base URL</span><code>{{H(string.IsNullOrWhiteSpace(state.PartnerRegistrationAudience) ? "None" : state.PartnerRegistrationAudience)}}</code></div>
          <div><span>Last OAuth</span><code>{{FormatDate(state.LastOAuthUtc)}}</code></div>
          <div><span>Last token refresh</span><code>{{FormatDate(state.LastTokenRefreshUtc)}}</code></div>
          <div><span>Last vehicle check</span><code>{{FormatDate(state.LastVehicleDiagnosticsUtc)}}</code></div>
          <div><span>Vehicle check summary</span><code>{{H(string.IsNullOrWhiteSpace(state.LastVehicleDiagnosticsSummary) ? "None" : state.LastVehicleDiagnosticsSummary)}}</code></div>
          <div><span>Edge Gateway asset id</span><code>{{H(state.PublicAssetId?.ToString("D") ?? "None")}}</code></div>
          <div><span>Edge Gateway OAuth route id</span><code>{{H(state.PublicOAuthRouteId?.ToString("D") ?? "None")}}</code></div>
          <div><span>Private key path</span><code>{{H(string.IsNullOrWhiteSpace(state.PrivateKeyPath) ? "Generate a key first." : state.PrivateKeyPath)}}</code></div>
        </div>
        <div class="split-actions" style="margin-top:12px">
          {{diagnosticActions}}
        </div>
      </div>

      <div class="card">
        <h2>Public Key Preview</h2>
        <p class="meta">Only the public key should be published. The private key is exported separately as <code style="display:inline;padding:2px 5px">tesla_fleet.key</code> for Home Assistant.</p>
        <textarea readonly>{{H(string.IsNullOrWhiteSpace(state.PublicKeyPem) ? "Generate a key to see the public key." : state.PublicKeyPem)}}</textarea>
      </div>
    </section>
  </main>
</body>
</html>
""";
}

static string H(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

static string BuildStatusClass(string? status)
{
    var value = (status ?? string.Empty).ToLowerInvariant();
    if (value.Contains("verified", StringComparison.Ordinal) ||
        value.Contains("published", StringComparison.Ordinal) ||
        value.Contains("ready", StringComparison.Ordinal) ||
        value.Contains("saved", StringComparison.Ordinal) ||
        value.Contains("connected", StringComparison.Ordinal))
    {
        return "ready";
    }

    return value.Contains("failed", StringComparison.Ordinal) ||
           value.Contains("error", StringComparison.Ordinal)
        ? "fail"
        : "warn";
}

static string FormatDate(DateTimeOffset? value) =>
    value.HasValue ? H(value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) : "Never";

static string BuildPublicKeyUrl(string domain) =>
    string.IsNullOrWhiteSpace(domain)
        ? string.Empty
        : $"https://{domain.Trim().TrimEnd('.')}{TeslaPublicKeyPath}";

static string BuildOAuthStartUrl(string domain) =>
    TeslaFleetDefaults.BuildOAuthStartUrl(domain);

static string BuildOAuthRedirectUri(string domain) =>
    TeslaFleetDefaults.BuildOAuthRedirectUri(domain);

static string BuildTeslaAuthorizeUrl(TeslaFleetState state, string oauthState, string nonce)
{
    var redirectUri = BuildOAuthRedirectUri(state.OriginDomain);
    var query = new Dictionary<string, string>
    {
        ["response_type"] = "code",
        ["client_id"] = state.TeslaClientId,
        ["redirect_uri"] = redirectUri,
        ["scope"] = NormalizeScopes(state.TeslaScopes),
        ["state"] = oauthState,
        ["nonce"] = nonce,
        ["locale"] = "en-US",
        ["prompt"] = "login",
        ["prompt_missing_scopes"] = "true",
        ["show_keypair_step"] = "true"
    };

    return $"{TeslaAuthorizeEndpoint}?{string.Join('&', query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))}";
}

static string NormalizeScopes(string value)
{
    var scopes = (value ?? string.Empty)
        .Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (scopes.Length == 0)
    {
        return DefaultTeslaScopes;
    }

    if (!scopes.Contains("openid", StringComparer.Ordinal))
    {
        scopes = ["openid", .. scopes];
    }

    if (!scopes.Contains("offline_access", StringComparer.Ordinal))
    {
        scopes = [.. scopes, "offline_access"];
    }

    return string.Join(' ', scopes);
}

static string CreateUrlSafeToken()
{
    var bytes = RandomNumberGenerator.GetBytes(32);
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace("+", "-", StringComparison.Ordinal)
        .Replace("/", "_", StringComparison.Ordinal);
}

static string FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

static string ResolveFleetApiAudience(string configuredAudience, string accessToken)
    =>
    TeslaFleetDefaults.ResolveFleetApiAudience(configuredAudience, accessToken);

static string RenderOAuthCompletePage(TeslaFleetState state)
{
    var virtualKeyUrl = string.IsNullOrWhiteSpace(state.OriginDomain)
        ? string.Empty
        : $"https://tesla.com/_ak/{state.OriginDomain}";
    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Tesla OAuth Connected</title>
  <style>{{SimplePageCss()}}</style>
</head>
<body>
  <main>
    <h1>Tesla OAuth Connected</h1>
    <p>The Tesla authorization completed and tokens were saved locally in {{ProductName}}.</p>
    <div class="box">
      <span>Virtual key install URL</span>
      <code>{{H(virtualKeyUrl)}}</code>
    </div>
    <div class="actions">
      <a href="/">Back to Tesla Fleet Helper</a>
      <a href="{{H(virtualKeyUrl)}}">Install virtual key</a>
    </div>
  </main>
</body>
</html>
""";
}

static string RenderSimplePage(string title, string message) =>
    $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{H(title)}}</title>
  <style>{{SimplePageCss()}}</style>
</head>
<body>
  <main>
    <h1>{{H(title)}}</h1>
    <p>{{H(message)}}</p>
    <div class="actions"><a href="/">Back to Tesla Fleet Helper</a></div>
  </main>
</body>
</html>
""";

static string SimplePageCss() =>
    """
    :root { color-scheme: dark light; --bg:#0f141b; --surface:#161d27; --border:#334155; --text:#eef2f7; --muted:#a8b3c2; --accent:#4fd1c5; }
    * { box-sizing: border-box; }
    body { margin:0; min-height:100vh; display:grid; place-items:center; background:var(--bg); color:var(--text); font-family:Inter,ui-sans-serif,system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
    main { width:min(720px, calc(100vw - 32px)); background:var(--surface); border:1px solid var(--border); border-radius:8px; padding:24px; }
    h1 { margin:0 0 10px; font-size:26px; }
    p { margin:0 0 16px; color:var(--muted); line-height:1.5; }
    .box { display:grid; gap:8px; margin:16px 0; }
    .box span { color:var(--muted); font-size:13px; }
    code { display:block; overflow-wrap:anywhere; border:1px solid var(--border); border-radius:7px; padding:10px 12px; color:#8bd5ff; background:#0c1118; }
    .actions { display:flex; flex-wrap:wrap; gap:10px; }
    a { display:inline-flex; min-height:40px; align-items:center; justify-content:center; border:1px solid var(--border); border-radius:7px; padding:9px 13px; background:#1d2734; color:var(--text); text-decoration:none; font-weight:650; }
    a:last-child { background:var(--accent); border-color:var(--accent); color:#061316; }
    """;

static string NormalizeHttpUrl(string value, string defaultUrl = "http://127.0.0.1:5000")
{
    var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return defaultUrl;
    }

    if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException("Enter a valid Edge Gateway API URL.");
    }

    return uri.ToString().TrimEnd('/');
}

static string NormalizeDomain(string value, bool required)
{
    var trimmed = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter a valid domain.");
        }

        trimmed = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return required ? throw new InvalidOperationException("Enter the Tesla origin domain.") : string.Empty;
    }

    if (!trimmed.Contains('.', StringComparison.Ordinal) ||
        trimmed.Contains('*', StringComparison.Ordinal) ||
        trimmed.Contains('/', StringComparison.Ordinal) ||
        trimmed.Contains('\\', StringComparison.Ordinal) ||
        trimmed.Contains(' ', StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Enter a valid DNS hostname, for example tesla.example.com.");
    }

    return trimmed;
}

sealed class TeslaFleetStore(IConfiguration configuration, IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string dataRoot = ResolvePath(
        configuration["TeslaFleetHelper:DataRoot"] ?? DefaultDataRoot(environment),
        environment);
    private readonly string optionsJsonPath = ResolvePath(
        configuration["TeslaFleetHelper:OptionsJsonPath"] ?? DefaultOptionsJsonPath(environment),
        environment);
    private readonly string configuredEdgeGatewayUrl = TeslaFleetDefaults.NormalizeHttpUrl(
        configuration["TeslaFleetHelper:EdgeGatewayUrl"] ?? "http://127.0.0.1:5000");
    private readonly string configuredPublicUpstreamUrl = TeslaFleetDefaults.NormalizeHttpUrl(
        configuration["TeslaFleetHelper:PublicUpstreamUrl"] ?? "http://127.0.0.1:5055");

    public string PrivateKeyPath => Path.Combine(dataRoot, "secrets", "tesla_fleet.key");

    private string StatePath => Path.Combine(dataRoot, "state.json");

    public async Task<TeslaFleetState> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataRoot);
        if (!File.Exists(StatePath))
        {
            return new TeslaFleetState(EdgeGatewayUrl: ReadDefaultEdgeGatewayUrl());
        }

        await using var stream = File.OpenRead(StatePath);
        var state = await JsonSerializer.DeserializeAsync<TeslaFleetState>(stream, JsonOptions, cancellationToken) ??
                    new TeslaFleetState();
        return state with
        {
            EdgeGatewayUrl = string.IsNullOrWhiteSpace(state.EdgeGatewayUrl)
                ? ReadDefaultEdgeGatewayUrl()
                : state.EdgeGatewayUrl,
            PublicUpstreamUrl = string.IsNullOrWhiteSpace(state.PublicUpstreamUrl)
                ? configuredPublicUpstreamUrl
                : state.PublicUpstreamUrl,
            FleetApiAudience = string.IsNullOrWhiteSpace(state.FleetApiAudience)
                ? TeslaFleetDefaults.DefaultFleetApiAudience
                : TeslaFleetDefaults.ResolveFleetApiAudience(state.FleetApiAudience, state.AccessToken),
            TeslaScopes = string.IsNullOrWhiteSpace(state.TeslaScopes)
                ? "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds"
                : state.TeslaScopes,
            PublicKeyUrl = string.IsNullOrWhiteSpace(state.PublicKeyUrl) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildPublicKeyUrl(state.OriginDomain)
                : state.PublicKeyUrl,
            OAuthStartUrl = string.IsNullOrWhiteSpace(state.OAuthStartUrl) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildOAuthStartUrl(state.OriginDomain)
                : state.OAuthStartUrl,
            OAuthRedirectUri = ShouldRefreshOAuthRedirectUri(state.OAuthRedirectUri) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildOAuthRedirectUri(state.OriginDomain)
                : state.OAuthRedirectUri,
            LastChecks = state.LastChecks ?? []
        };
    }

    public async Task SaveAsync(TeslaFleetState state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataRoot);
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, StatePath, overwrite: true);
    }

    private string ReadDefaultEdgeGatewayUrl()
    {
        try
        {
            if (File.Exists(optionsJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(optionsJsonPath));
                if (document.RootElement.TryGetProperty("edge_gateway_url", out var url) &&
                    !string.IsNullOrWhiteSpace(url.GetString()))
                {
                    return TeslaFleetDefaults.NormalizeHttpUrl(url.GetString()!);
                }
            }
        }
        catch
        {
            // Fall back to the standard host-network Edge Gateway control-plane URL.
        }

        return configuredEdgeGatewayUrl;
    }

    private static bool ShouldRefreshOAuthRedirectUri(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Trim().EndsWith(TeslaFleetDefaults.OAuthCallbackAliasPath, StringComparison.OrdinalIgnoreCase);

    private static string DefaultDataRoot(IWebHostEnvironment environment) =>
        !environment.IsDevelopment() && CanWriteToDirectory("/data")
            ? "/data/lms-tesla-fleet-helper"
            : Path.Combine(environment.ContentRootPath, "data", "lms-tesla-fleet-helper");

    private static string DefaultOptionsJsonPath(IWebHostEnvironment environment) =>
        !environment.IsDevelopment() && Directory.Exists("/data")
            ? "/data/options.json"
            : Path.Combine(environment.ContentRootPath, "data", "options.json");

    private static string ResolvePath(string path, IWebHostEnvironment environment) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path, environment.ContentRootPath);

    private static bool CanWriteToDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            var probePath = Path.Combine(path, $".lms-tesla-fleet-helper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(probePath);
            Directory.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

sealed class EdgeGatewayPublicAssetClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PublicAssetPublishResponse> PublishAsync(
        string edgeGatewayUrl,
        string originDomain,
        string publicKeyPem,
        CancellationToken cancellationToken)
    {
        var payload = new PublicAssetPublishRequest(
            originDomain,
            TeslaFleetDefaults.PublicKeyPath,
            TeslaFleetDefaults.PublicKeyContentType,
            publicKeyPem,
            "Tesla Fleet public key",
            "no-store");
        using var response = await httpClient.PostAsJsonAsync(
            $"{edgeGatewayUrl.TrimEnd('/')}/api/public-assets/publish",
            payload,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = Deserialize<PublicAssetPublishResponse>(body);
        if (result is not null)
        {
            return result with
            {
                Summary = string.IsNullOrWhiteSpace(result.Summary)
                    ? $"Edge Gateway returned HTTP {(int)response.StatusCode}."
                    : result.Summary
            };
        }

        return new PublicAssetPublishResponse(
            response.IsSuccessStatusCode,
            $"Edge Gateway returned HTTP {(int)response.StatusCode}. {body}",
            [],
            null);
    }

    public async Task<PublicAssetVerifyResponse> VerifyAsync(
        string edgeGatewayUrl,
        Guid publicAssetId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"{edgeGatewayUrl.TrimEnd('/')}/api/public-assets/{publicAssetId:D}/verify",
            null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = Deserialize<PublicAssetVerifyResponse>(body);
        if (result is not null)
        {
            return result;
        }

        return new PublicAssetVerifyResponse(
            response.IsSuccessStatusCode,
            response.IsSuccessStatusCode ? "Verified" : "Failed",
            $"Edge Gateway returned HTTP {(int)response.StatusCode}. {body}",
            []);
    }

    public async Task<PublicProxyRoutePublishResponse> PublishOAuthRouteAsync(
        string edgeGatewayUrl,
        string originDomain,
        string upstreamUrl,
        CancellationToken cancellationToken)
    {
        var startRoute = await PublishPublicRouteAsync(
            edgeGatewayUrl,
            originDomain,
            TeslaFleetDefaults.OAuthRoutePath,
            upstreamUrl,
            "Tesla Fleet Helper OAuth start endpoints",
            matchSubpaths: true,
            cancellationToken);
        var callbackRoute = await PublishPublicRouteAsync(
            edgeGatewayUrl,
            originDomain,
            TeslaFleetDefaults.OAuthCallbackPath,
            upstreamUrl,
            "Tesla Fleet Helper OAuth callback endpoint",
            matchSubpaths: false,
            cancellationToken);
        return new PublicProxyRoutePublishResponse(
            startRoute.Succeeded && callbackRoute.Succeeded,
            $"{startRoute.Summary} {callbackRoute.Summary}".Trim(),
            startRoute.Warnings.Concat(callbackRoute.Warnings).ToList(),
            callbackRoute.Route ?? startRoute.Route);
    }

    private async Task<PublicProxyRoutePublishResponse> PublishPublicRouteAsync(
        string edgeGatewayUrl,
        string originDomain,
        string pathPrefix,
        string upstreamUrl,
        string description,
        bool matchSubpaths,
        CancellationToken cancellationToken)
    {
        var payload = new PublicProxyRoutePublishRequest(
            originDomain,
            pathPrefix,
            upstreamUrl,
            description,
            Enabled: true,
            RequiresAuth: false,
            PreserveHostHeader: true,
            StripForwardedFor: true,
            MatchSubpaths: matchSubpaths);
        using var response = await httpClient.PostAsJsonAsync(
            $"{edgeGatewayUrl.TrimEnd('/')}/api/public-routes/publish",
            payload,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = Deserialize<PublicProxyRoutePublishResponse>(body);
        if (result is not null)
        {
            return result with
            {
                Summary = string.IsNullOrWhiteSpace(result.Summary)
                    ? $"Edge Gateway returned HTTP {(int)response.StatusCode}."
                    : result.Summary
            };
        }

        return new PublicProxyRoutePublishResponse(
            response.IsSuccessStatusCode,
            $"Edge Gateway returned HTTP {(int)response.StatusCode}. {body}",
            [],
            null);
    }

    public async Task<PublicAssetVerifyResponse> VerifyPublicUrlAsync(
        string publicUrl,
        string expectedPublicKeyPem,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            return new PublicAssetVerifyResponse(false, "Missing", "Publish the public key before verifying.", []);
        }

        using var response = await httpClient.GetAsync(publicUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var checks = new List<string>
        {
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
            $"Content-Type: {response.Content.Headers.ContentType}."
        };
        var success = response.IsSuccessStatusCode &&
                      body.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) &&
                      body.Trim().Equals(expectedPublicKeyPem.Trim(), StringComparison.Ordinal);
        return new PublicAssetVerifyResponse(
            success,
            success ? "Verified" : "Failed",
            success ? "Public URL returned the Tesla Fleet public key." : "Public URL did not return the expected public key.",
            checks);
    }

    private static T? Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}

sealed class TeslaFleetOAuthClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TeslaTokenResponse> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        string audience,
        string scopes,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["code"] = code.Trim(),
            ["audience"] = string.IsNullOrWhiteSpace(audience) ? TeslaFleetDefaults.DefaultFleetApiAudience : audience.Trim(),
            ["redirect_uri"] = redirectUri.Trim(),
            ["scope"] = string.IsNullOrWhiteSpace(scopes)
                ? "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds"
                : scopes.Trim()
        });
        using var response = await httpClient.PostAsync("https://fleet-auth.prd.vn.cloud.tesla.com/oauth2/v3/token", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        TeslaTokenResponse? token = null;
        try
        {
            token = JsonSerializer.Deserialize<TeslaTokenResponse>(body, JsonOptions);
        }
        catch
        {
            // Fall through to the HTTP error below with the raw response summary.
        }

        if (!response.IsSuccessStatusCode || token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException($"Tesla token exchange returned HTTP {(int)response.StatusCode}: {body}");
        }

        return token;
    }

    public async Task<TeslaTokenResponse> RefreshAccessTokenAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        string audience,
        string scopes,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["refresh_token"] = refreshToken.Trim(),
            ["audience"] = string.IsNullOrWhiteSpace(audience) ? TeslaFleetDefaults.DefaultFleetApiAudience : audience.Trim(),
            ["scope"] = string.IsNullOrWhiteSpace(scopes)
                ? "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds"
                : scopes.Trim()
        });
        using var response = await httpClient.PostAsync("https://fleet-auth.prd.vn.cloud.tesla.com/oauth2/v3/token", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        TeslaTokenResponse? token = null;
        try
        {
            token = JsonSerializer.Deserialize<TeslaTokenResponse>(body, JsonOptions);
        }
        catch
        {
            // Fall through to the HTTP error below with the raw response summary.
        }

        if (!response.IsSuccessStatusCode || token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException($"Tesla token refresh returned HTTP {(int)response.StatusCode}: {body}");
        }

        return token;
    }
}

sealed class TeslaFleetPartnerClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TeslaPartnerRegistrationResult> RegisterPartnerAccountAsync(
        string clientId,
        string clientSecret,
        string originDomain,
        string audience,
        CancellationToken cancellationToken)
    {
        var normalizedAudience = TeslaFleetDefaults.NormalizeHttpUrl(audience, TeslaFleetDefaults.EuFleetApiAudience);
        var token = await GetPartnerTokenAsync(clientId, clientSecret, normalizedAudience, cancellationToken);
        var payload = new TeslaPartnerAccountRegisterRequest(originDomain);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{normalizedAudience.TrimEnd('/')}/api/1/partner_accounts")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var checks = new List<string>
        {
            $"Partner token acquired for {normalizedAudience}.",
            $"Tesla Partner Account register HTTP {(int)response.StatusCode} {response.ReasonPhrase}."
        };

        if (!response.IsSuccessStatusCode)
        {
            return new TeslaPartnerRegistrationResult(
                false,
                normalizedAudience,
                $"Tesla Partner Account registration returned HTTP {(int)response.StatusCode}: {body}",
                checks);
        }

        checks.Add($"Registered origin domain {originDomain} with Tesla Partner Accounts.");
        return new TeslaPartnerRegistrationResult(
            true,
            normalizedAudience,
            $"Tesla Partner Account registered for {originDomain}.",
            checks);
    }

    public async Task<TeslaPartnerPublicKeyResult> GetRegisteredPublicKeyAsync(
        string clientId,
        string clientSecret,
        string originDomain,
        string audience,
        CancellationToken cancellationToken)
    {
        var normalizedAudience = TeslaFleetDefaults.NormalizeHttpUrl(audience, TeslaFleetDefaults.EuFleetApiAudience);
        var token = await GetPartnerTokenAsync(clientId, clientSecret, normalizedAudience, cancellationToken);
        var url = $"{normalizedAudience.TrimEnd('/')}/api/1/partner_accounts/public_key?domain={Uri.EscapeDataString(originDomain)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var checks = new List<string>
        {
            $"Partner token acquired for {normalizedAudience}.",
            $"Tesla Partner Account public-key lookup HTTP {(int)response.StatusCode} {response.ReasonPhrase}."
        };

        if (!response.IsSuccessStatusCode)
        {
            return new TeslaPartnerPublicKeyResult(
                false,
                normalizedAudience,
                string.Empty,
                $"Tesla public-key lookup returned HTTP {(int)response.StatusCode}: {TruncateForDiagnostics(body)}",
                checks);
        }

        var publicKey = TryReadPublicKeyHex(body);
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return new TeslaPartnerPublicKeyResult(
                false,
                normalizedAudience,
                string.Empty,
                "Tesla public-key lookup did not include a public key.",
                checks);
        }

        checks.Add($"Tesla returned a public key for {originDomain}.");
        return new TeslaPartnerPublicKeyResult(
            true,
            normalizedAudience,
            publicKey,
            $"Tesla Partner Account public key is registered for {originDomain}.",
            checks);
    }

    private static string TryReadPublicKeyHex(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryReadPublicKeyHex(document.RootElement, out var publicKey))
            {
                return publicKey;
            }
        }
        catch
        {
            // Handled by returning empty and reporting the lookup as missing a key.
        }

        return string.Empty;
    }

    private static bool TryReadPublicKeyHex(JsonElement element, out string publicKey)
    {
        publicKey = string.Empty;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((property.NameEquals("public_key") ||
                     property.NameEquals("publicKey") ||
                     property.NameEquals("key")) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    publicKey = (property.Value.GetString() ?? string.Empty).Trim();
                    return !string.IsNullOrWhiteSpace(publicKey);
                }

                if ((property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) &&
                    TryReadPublicKeyHex(property.Value, out publicKey))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryReadPublicKeyHex(item, out publicKey))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";

    private async Task<string> GetPartnerTokenAsync(
        string clientId,
        string clientSecret,
        string audience,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["audience"] = audience.Trim(),
            ["scope"] = "openid vehicle_device_data vehicle_cmds vehicle_charging_cmds"
        });
        using var response = await httpClient.PostAsync("https://fleet-auth.prd.vn.cloud.tesla.com/oauth2/v3/token", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        TeslaTokenResponse? token = null;
        try
        {
            token = JsonSerializer.Deserialize<TeslaTokenResponse>(body, JsonOptions);
        }
        catch
        {
            // Fall through to the HTTP error below with the raw response summary.
        }

        if (!response.IsSuccessStatusCode || token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException($"Tesla partner token request returned HTTP {(int)response.StatusCode}: {body}");
        }

        return token.AccessToken;
    }
}

sealed class TeslaFleetApiClient(HttpClient httpClient)
{
    public async Task<TeslaVehicleDiagnosticsResult> GetVehiclesAsync(
        string fleetApiAudience,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var normalizedAudience = TeslaFleetDefaults.NormalizeHttpUrl(fleetApiAudience, TeslaFleetDefaults.DefaultFleetApiAudience);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{normalizedAudience.TrimEnd('/')}/api/1/vehicles");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var checks = new List<string>
        {
            $"Tesla vehicles API HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
            $"Fleet API base URL: {normalizedAudience}."
        };

        if (!response.IsSuccessStatusCode)
        {
            checks.Add($"Response: {TruncateForDiagnostics(body)}");
            return new TeslaVehicleDiagnosticsResult(
                false,
                $"Tesla vehicles API returned HTTP {(int)response.StatusCode}.",
                checks,
                0);
        }

        var vehicles = ReadVehicleSummaries(body);
        checks.Add($"Vehicle count: {vehicles.Count}.");
        checks.AddRange(vehicles.Take(8));
        if (vehicles.Count > 8)
        {
            checks.Add($"Showing first 8 of {vehicles.Count} vehicles.");
        }

        return new TeslaVehicleDiagnosticsResult(
            true,
            vehicles.Count == 0
                ? "Tesla OAuth is valid, but no vehicles were returned."
                : $"Tesla OAuth is valid and returned {vehicles.Count} vehicle(s).",
            checks,
            vehicles.Count);
    }

    private static List<string> ReadVehicleSummaries(string body)
    {
        var vehicles = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var response = root.TryGetProperty("response", out var responseElement)
                ? responseElement
                : root;
            if (response.ValueKind != JsonValueKind.Array)
            {
                return vehicles;
            }

            foreach (var vehicle in response.EnumerateArray())
            {
                var name = ReadString(vehicle, "display_name", "vin", "id_s") ?? "Unnamed vehicle";
                var vin = ReadString(vehicle, "vin");
                var state = ReadString(vehicle, "state") ?? "unknown state";
                var commandProtocol = ReadString(vehicle, "vehicle_command_protocol_required", "command_protocol_required", "command_signing");
                var commandText = string.IsNullOrWhiteSpace(commandProtocol)
                    ? "command protocol not reported"
                    : $"command protocol: {commandProtocol}";
                vehicles.Add(string.IsNullOrWhiteSpace(vin)
                    ? $"{name}: {state}; {commandText}."
                    : $"{name} ({MaskVin(vin)}): {state}; {commandText}.");
            }
        }
        catch (Exception exception)
        {
            vehicles.Add($"Vehicle response parse failed: {exception.Message}");
        }

        return vehicles;
    }

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => property.GetRawText(),
                _ => property.GetRawText()
            };
        }

        return null;
    }

    private static string MaskVin(string vin)
    {
        var trimmed = vin.Trim();
        return trimmed.Length <= 7 ? "VIN saved" : $"...{trimmed[^6..]}";
    }

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";
}

sealed record TeslaAccessTokenResult(
    TeslaFleetState State,
    bool Refreshed,
    List<string> Checks);

sealed record TeslaPartnerPublicKeyResult(
    bool Succeeded,
    string Audience,
    string PublicKeyHex,
    string Summary,
    List<string> Checks);

sealed record TeslaVehicleDiagnosticsResult(
    bool Succeeded,
    string Summary,
    List<string> Checks,
    int VehicleCount);

sealed record TeslaFleetState(
    string EdgeGatewayUrl = "http://127.0.0.1:5000",
    string PublicUpstreamUrl = "http://127.0.0.1:5055",
    string OriginDomain = "",
    string TeslaClientId = "",
    string TeslaClientSecret = "",
    string FleetApiAudience = "https://fleet-api.prd.na.vn.cloud.tesla.com",
    string TeslaScopes = "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds",
    string PublicKeyPem = "",
    string PrivateKeyPath = "",
    DateTimeOffset? KeyGeneratedUtc = null,
    Guid? PublicAssetId = null,
    Guid? PublicOAuthRouteId = null,
    string PublicKeyUrl = "",
    string OAuthStartUrl = "",
    string OAuthRedirectUri = "",
    string OAuthState = "",
    string OAuthNonce = "",
    DateTimeOffset? OAuthStartedUtc = null,
    string AccessToken = "",
    string RefreshToken = "",
    string TokenType = "",
    DateTimeOffset? TokenExpiresUtc = null,
    DateTimeOffset? LastPublishedUtc = null,
    DateTimeOffset? LastVerifiedUtc = null,
    DateTimeOffset? LastPartnerRegistrationUtc = null,
    DateTimeOffset? LastOAuthUtc = null,
    DateTimeOffset? LastTokenRefreshUtc = null,
    DateTimeOffset? LastVehicleDiagnosticsUtc = null,
    string LastVehicleDiagnosticsSummary = "",
    string PartnerRegistrationAudience = "",
    string PartnerRegistrationStatus = "",
    string PartnerRegistrationMessage = "",
    string LastStatus = "",
    string LastMessage = "",
    List<string>? LastChecks = null);

sealed record PublicAssetPublishRequest(
    string Hostname,
    string Path,
    string ContentType,
    string Content,
    string Description,
    string CacheControl);

sealed record PublicAssetPublishResponse(
    bool Succeeded,
    string Summary,
    List<string> Warnings,
    PublicAssetItem? Asset);

sealed record PublicAssetVerifyResponse(
    bool Succeeded,
    string Status,
    string Message,
    List<string> Checks);

sealed record PublicProxyRoutePublishRequest(
    string Hostname,
    string PathPrefix,
    string UpstreamUrl,
    string Description,
    bool Enabled,
    bool RequiresAuth,
    bool PreserveHostHeader,
    bool StripForwardedFor,
    bool MatchSubpaths);

sealed record PublicProxyRoutePublishResponse(
    bool Succeeded,
    string Summary,
    List<string> Warnings,
    PublicProxyRouteItem? Route)
{
    public static PublicProxyRoutePublishResponse Failure(string summary) =>
        new(false, summary, [], null);
}

sealed record PublicProxyRouteItem(
    Guid Id,
    string Hostname,
    string PathPrefix,
    string UpstreamUrl,
    string Description,
    bool Enabled,
    bool RequiresAuth,
    bool PreserveHostHeader,
    bool StripForwardedFor,
    bool MatchSubpaths,
    string PublicUrl);

sealed record PublicAssetItem(
    Guid Id,
    string Hostname,
    string Path,
    string ContentType,
    string PublicUrl,
    bool Enabled,
    bool RequiresAuth,
    string CacheControl);

sealed record TeslaPartnerRegistrationResult(
    bool Succeeded,
    string Audience,
    string Summary,
    List<string> Checks)
{
    public static TeslaPartnerRegistrationResult Failure(string summary) =>
        new(false, string.Empty, summary, []);
}

sealed record TeslaPartnerAccountRegisterRequest(string Domain);

sealed record TeslaTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("id_token")] string IdToken = "");

static class TeslaFleetDefaults
{
    public const string DefaultFleetApiAudience = "https://fleet-api.prd.na.vn.cloud.tesla.com";
    public const string EuFleetApiAudience = "https://fleet-api.prd.eu.vn.cloud.tesla.com";
    public const string PublicKeyPath = "/.well-known/appspecific/com.tesla.3p.public-key.pem";
    public const string PublicKeyContentType = "application/x-pem-file";
    public const string OAuthRoutePath = "/oauth";
    public const string OAuthStartPath = "/oauth/start";
    public const string OAuthCallbackPath = "/redirect";
    public const string OAuthCallbackAliasPath = "/oauth/callback";

    public static string BuildPublicKeyUrl(string domain) =>
        string.IsNullOrWhiteSpace(domain)
            ? string.Empty
            : $"https://{domain.Trim().TrimEnd('.')}{PublicKeyPath}";

    public static string BuildOAuthStartUrl(string domain) =>
        string.IsNullOrWhiteSpace(domain)
            ? string.Empty
            : $"https://{domain.Trim().TrimEnd('.')}{OAuthStartPath}";

    public static string BuildOAuthRedirectUri(string domain) =>
        string.IsNullOrWhiteSpace(domain)
            ? string.Empty
            : $"https://{domain.Trim().TrimEnd('.')}{OAuthCallbackPath}";

    public static string NormalizeHttpUrl(string value, string defaultUrl = "http://127.0.0.1:5000")
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return defaultUrl;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Enter a valid Edge Gateway API URL.");
        }

        return uri.ToString().TrimEnd('/');
    }

    public static string ResolveFleetApiAudience(string configuredAudience, string accessToken)
    {
        var configured = string.IsNullOrWhiteSpace(configuredAudience)
            ? DefaultFleetApiAudience
            : configuredAudience.Trim().TrimEnd('/');
        var tokenRegion = TryGetTeslaTokenRegion(accessToken);
        return tokenRegion.Equals("EU", StringComparison.OrdinalIgnoreCase)
            ? EuFleetApiAudience
            : configured;
    }

    private static string TryGetTeslaTokenRegion(string accessToken)
    {
        try
        {
            var parts = (accessToken ?? string.Empty).Split('.');
            if (parts.Length < 2)
            {
                return string.Empty;
            }

            var payloadBytes = Convert.FromBase64String(PadBase64Url(parts[1]));
            using var document = JsonDocument.Parse(payloadBytes);
            if (document.RootElement.TryGetProperty("ou_code", out var region) &&
                !string.IsNullOrWhiteSpace(region.GetString()))
            {
                return region.GetString()!;
            }

            if (document.RootElement.TryGetProperty("aud", out var audience) &&
                audience.ValueKind == JsonValueKind.Array &&
                audience.EnumerateArray().Any(item =>
                    (item.GetString() ?? string.Empty).Contains(".prd.eu.", StringComparison.OrdinalIgnoreCase)))
            {
                return "EU";
            }
        }
        catch
        {
            // Region inference is best effort. The configured Fleet API base URL remains authoritative.
        }

        return string.Empty;
    }

    private static string PadBase64Url(string value)
    {
        var normalized = value
            .Replace('-', '+')
            .Replace('_', '/');
        return normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
    }
}
