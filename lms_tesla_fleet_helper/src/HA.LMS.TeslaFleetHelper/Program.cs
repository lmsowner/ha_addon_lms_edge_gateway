using System.Net.Http.Headers;
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
const string DefaultTeslaScopes = "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds energy_device_data energy_cmds";
const string EnergyDeviceDataScope = "energy_device_data";
const string EnergyCommandsScope = "energy_cmds";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://0.0.0.0:5055");
builder.Services.AddSingleton<TeslaFleetStore>();
builder.Services.AddSingleton<TeslaFleetTokenCoordinator>();
builder.Services.AddSingleton<TeslaFleetMqttPublisher>();
builder.Services.AddSingleton<TeslaFleetPropertyHarness>();
builder.Services.AddSingleton<TeslaFleetStateMapper>();
builder.Services.AddSingleton<HomeAssistantMqttProjectionMapper>();
builder.Services.AddHostedService<TeslaFleetVehicleCommandProxyService>();
builder.Services.AddHostedService<TeslaFleetHomeAssistantPublisherService>();
builder.Services.AddHostedService<TeslaFleetHomeAssistantCommandService>();
builder.Services.AddHttpClient<EdgeGatewayCompanionResolver>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});
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
builder.Services.AddHttpClient<TeslaFleetDataClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});
builder.Services.AddHttpClient<TeslaFleetEnergyCommandClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
});
builder.Services.AddHttpClient<TeslaFleetVehicleCommandClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(75);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LMS-Tesla-Fleet-Helper");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", product = ProductName }));

app.MapGet("/", async (
    TeslaFleetStore store,
    EdgeGatewayCompanionResolver companionResolver,
    CancellationToken cancellationToken) =>
{
    var state = ApplyCompanionDefaults(await store.LoadAsync(cancellationToken));
    var companion = await companionResolver.ResolveAsync(cancellationToken);
    return Results.Content(RenderPage(state, companion), "text/html; charset=utf-8");
});

app.MapPost("/actions/save-settings", async (
    HttpContext context,
    TeslaFleetStore store) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var state = await store.LoadAsync();
    try
    {
        var hasOriginDomain = form.ContainsKey("origin_domain");
        var hasTeslaClientId = form.ContainsKey("tesla_client_id");
        var hasTeslaClientSecret = form.ContainsKey("tesla_client_secret");
        var hasFleetApiAudience = form.ContainsKey("fleet_api_audience");
        var hasTeslaScopes = form.ContainsKey("tesla_scopes");
        var hasHomeAssistantMqttEnabled = form.ContainsKey("ha_mqtt_enabled_present") || form.ContainsKey("ha_mqtt_enabled");
        var hasFetchRealtimeVehicleData = form.ContainsKey("fetch_realtime_vehicle_data_present") || form.ContainsKey("fetch_realtime_vehicle_data");
        var hasMqttHost = form.ContainsKey("mqtt_host");
        var hasMqttPort = form.ContainsKey("mqtt_port");
        var hasMqttUsername = form.ContainsKey("mqtt_username");
        var hasMqttPassword = form.ContainsKey("mqtt_password");
        var hasMqttDiscoveryPrefix = form.ContainsKey("mqtt_discovery_prefix");
        var hasMqttBaseTopic = form.ContainsKey("mqtt_base_topic");
        var hasHomeAssistantRefreshInterval = form.ContainsKey("ha_refresh_interval_minutes");

        state = state with
        {
            EdgeGatewayUrl = TeslaFleetDefaults.LocalEdgeGatewayUrl,
            OriginDomain = hasOriginDomain
                ? NormalizeDomain(form["origin_domain"].ToString(), required: false)
                : state.OriginDomain,
            PublicUpstreamUrl = TeslaFleetDefaults.LocalHelperUpstreamUrl,
            TeslaClientId = hasTeslaClientId ? form["tesla_client_id"].ToString().Trim() : state.TeslaClientId,
            TeslaClientSecret = !hasTeslaClientSecret || string.IsNullOrWhiteSpace(form["tesla_client_secret"].ToString())
                ? state.TeslaClientSecret
                : form["tesla_client_secret"].ToString().Trim(),
            FleetApiAudience = hasFleetApiAudience
                ? NormalizeHttpUrl(form["fleet_api_audience"].ToString(), DefaultFleetApiAudience)
                : state.FleetApiAudience,
            TeslaScopes = hasTeslaScopes ? NormalizeScopes(form["tesla_scopes"].ToString()) : state.TeslaScopes,
            HomeAssistantMqttEnabled = hasHomeAssistantMqttEnabled
                ? IsChecked(form["ha_mqtt_enabled"].ToString())
                : state.HomeAssistantMqttEnabled,
            FetchRealtimeVehicleData = hasFetchRealtimeVehicleData
                ? IsChecked(form["fetch_realtime_vehicle_data"].ToString())
                : state.FetchRealtimeVehicleData,
            MqttHost = hasMqttHost ? NormalizeHost(form["mqtt_host"].ToString(), "core-mosquitto") : state.MqttHost,
            MqttPort = hasMqttPort ? NormalizePort(form["mqtt_port"].ToString(), 1883) : state.MqttPort,
            MqttUsername = hasMqttUsername ? form["mqtt_username"].ToString().Trim() : state.MqttUsername,
            MqttPassword = !hasMqttPassword || string.IsNullOrWhiteSpace(form["mqtt_password"].ToString())
                ? state.MqttPassword
                : form["mqtt_password"].ToString(),
            MqttDiscoveryPrefix = hasMqttDiscoveryPrefix
                ? NormalizeTopicRoot(form["mqtt_discovery_prefix"].ToString(), "homeassistant")
                : state.MqttDiscoveryPrefix,
            MqttBaseTopic = hasMqttBaseTopic
                ? NormalizeTopicRoot(form["mqtt_base_topic"].ToString(), "lms/tesla-fleet")
                : state.MqttBaseTopic,
            HomeAssistantRefreshIntervalMinutes = hasHomeAssistantRefreshInterval
                ? NormalizeInt(
                    form["ha_refresh_interval_minutes"].ToString(),
                    defaultValue: 15,
                    min: 5,
                    max: 240)
                : state.HomeAssistantRefreshIntervalMinutes,
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
    return RedirectToAppRoot();
});

app.MapPost("/actions/check-companion-link", async (
    HttpContext context,
    TeslaFleetStore store,
    EdgeGatewayCompanionResolver companionResolver,
    EdgeGatewayPublicAssetClient edgeGatewayClient) =>
{
    var state = ApplyCompanionDefaults(await store.LoadAsync(context.RequestAborted));
    EdgeGatewayCompanionStatus? companion = null;
    try
    {
        companion = await companionResolver.ResolveAsync(context.RequestAborted);
        var result = await edgeGatewayClient.CheckCompanionLinkAsync(
            state.EdgeGatewayUrl,
            state.PublicUpstreamUrl,
            context.RequestAborted);
        state = state with
        {
            LastStatus = result.Succeeded ? "Companion link healthy" : "Companion link failed",
            LastMessage = result.Summary,
            LastChecks = companion.Checks.Concat(result.Checks).ToList()
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Companion link failed",
            LastMessage = exception.Message,
            LastChecks = companion?.Checks ?? []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return RedirectToAppRoot();
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

    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
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
    return RedirectToAppRoot();
});

app.MapPost("/actions/publish-ha", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetDataClient dataClient,
    TeslaFleetMqttPublisher mqttPublisher) =>
{
    var state = await store.LoadAsync();
    try
    {
        if (!state.HomeAssistantMqttEnabled)
        {
            throw new InvalidOperationException("Enable Home Assistant MQTT publishing and save settings first.");
        }

        var token = await tokenCoordinator.EnsureUsableAsync(state, context.RequestAborted);
        var snapshot = await dataClient.FetchSnapshotAsync(token.State, context.RequestAborted);
        var result = await mqttPublisher.PublishAsync(token.State, snapshot, context.RequestAborted);
        var checks = token.Checks.Concat(result.Checks).ToList();
        state = token.State with
        {
            LastHomeAssistantPublishUtc = result.Succeeded ? DateTimeOffset.UtcNow : state.LastHomeAssistantPublishUtc,
            LastHomeAssistantPublishSummary = result.Summary,
            LastStatus = result.Succeeded ? "Home Assistant published" : "Home Assistant publish failed",
            LastMessage = result.Summary,
            LastChecks = checks
        };
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Home Assistant publish failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return RedirectToAppRoot();
});

app.MapPost("/actions/discover-properties", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetPropertyHarness propertyHarness) =>
{
    var state = await store.LoadAsync();
    try
    {
        var run = await propertyHarness.DiscoverAsync(state, context.RequestAborted);
        state = ApplyPropertyDiscovery(run, "Tesla properties discovered");
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Property discovery failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return RedirectToAppRoot();
});

app.MapPost("/actions/clear-properties", async (
    HttpContext context,
    TeslaFleetStore store) =>
{
    var state = await store.LoadAsync();
    state = state with
    {
        DiscoveredProperties = [],
        LastPropertyDiscoveryUtc = null,
        LastPropertyDiscoverySummary = "",
        LastStatus = "Properties cleared",
        LastMessage = "Cleared the cached Tesla API property harness results.",
        LastChecks = []
    };
    await store.SaveAsync(state, context.RequestAborted);
    return RedirectToAppRoot();
});

app.MapPost("/actions/preview-ha-projection", async (
    HttpContext context,
    TeslaFleetStore store,
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetDataClient dataClient,
    TeslaFleetStateMapper stateMapper,
    HomeAssistantMqttProjectionMapper projectionMapper) =>
{
    var state = await store.LoadAsync();
    try
    {
        var run = await BuildHomeAssistantProjectionPreviewAsync(
            state,
            tokenCoordinator,
            dataClient,
            stateMapper,
            projectionMapper,
            context.RequestAborted);
        state = ApplyHomeAssistantProjectionPreview(run, "Home Assistant projection previewed");
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Projection preview failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
    }

    await store.SaveAsync(state, context.RequestAborted);
    return RedirectToAppRoot();
});

app.MapGet("/api/test-harness/status", async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    return Results.Json(BuildPropertyHarnessStatus(state));
});

app.MapGet("/api/test-harness/properties", async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    return Results.Json(new
    {
        status = BuildPropertyHarnessStatus(state),
        properties = state.DiscoveredProperties ?? []
    });
});

app.MapGet("/api/test-harness/ha-projection", async (
    TeslaFleetStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    return Results.Json(new
    {
        lastPreviewUtc = state.LastHomeAssistantProjectionPreviewUtc,
        summary = string.IsNullOrWhiteSpace(state.LastHomeAssistantProjectionPreviewSummary)
            ? "No Home Assistant projection preview has been run yet."
            : state.LastHomeAssistantProjectionPreviewSummary,
        entities = state.HomeAssistantProjectionPreviewEntities ?? []
    });
});

app.MapPost("/api/test-harness/ha-projection/refresh", async (
    TeslaFleetStore store,
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetDataClient dataClient,
    TeslaFleetStateMapper stateMapper,
    HomeAssistantMqttProjectionMapper projectionMapper,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    try
    {
        var run = await BuildHomeAssistantProjectionPreviewAsync(
            state,
            tokenCoordinator,
            dataClient,
            stateMapper,
            projectionMapper,
            cancellationToken);
        state = ApplyHomeAssistantProjectionPreview(run, "Home Assistant projection previewed");
        await store.SaveAsync(state, cancellationToken);
        return Results.Json(new
        {
            succeeded = true,
            summary = run.Summary,
            checks = run.Checks,
            entities = state.HomeAssistantProjectionPreviewEntities ?? []
        });
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Projection preview failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
        await store.SaveAsync(state, cancellationToken);
        return Results.Json(new
        {
            succeeded = false,
            message = exception.Message
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/test-harness/discover", async (
    TeslaFleetStore store,
    TeslaFleetPropertyHarness propertyHarness,
    CancellationToken cancellationToken) =>
{
    var state = await store.LoadAsync(cancellationToken);
    try
    {
        var run = await propertyHarness.DiscoverAsync(state, cancellationToken);
        state = ApplyPropertyDiscovery(run, "Tesla properties discovered");
        await store.SaveAsync(state, cancellationToken);
        return Results.Json(new
        {
            succeeded = true,
            status = BuildPropertyHarnessStatus(state),
            checks = run.Checks,
            properties = state.DiscoveredProperties ?? []
        });
    }
    catch (Exception exception)
    {
        state = state with
        {
            LastStatus = "Property discovery failed",
            LastMessage = exception.Message,
            LastChecks = []
        };
        await store.SaveAsync(state, cancellationToken);
        return Results.Json(new
        {
            succeeded = false,
            status = BuildPropertyHarnessStatus(state),
            message = exception.Message
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
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
        var scopes = NormalizeScopes(state.TeslaScopes);
        var updated = state with
        {
            OriginDomain = originDomain,
            TeslaScopes = scopes,
            OAuthState = oauthState,
            OAuthNonce = nonce,
            OAuthStartedUtc = DateTimeOffset.UtcNow,
            OAuthRedirectUri = BuildOAuthRedirectUri(originDomain),
            OAuthStartUrl = BuildOAuthStartUrl(originDomain),
            LastStatus = "OAuth started",
            LastMessage = "Redirecting to Tesla authorization.",
            LastChecks =
            [
                $"Requesting Tesla OAuth scopes: {scopes}.",
                "Energy data reads require energy_device_data; Home Assistant write controls require energy_cmds."
            ]
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
            NormalizeScopes(state.TeslaScopes),
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

static TeslaFleetState ApplyPropertyDiscovery(TeslaPropertyDiscoveryRun run, string status) =>
    run.State with
    {
        DiscoveredProperties = run.Properties,
        LastPropertyDiscoveryUtc = DateTimeOffset.UtcNow,
        LastPropertyDiscoverySummary = run.Summary,
        LastStatus = status,
        LastMessage = run.Summary,
        LastChecks = run.Checks
    };

static async Task<HomeAssistantProjectionPreviewRun> BuildHomeAssistantProjectionPreviewAsync(
    TeslaFleetState state,
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetDataClient dataClient,
    TeslaFleetStateMapper stateMapper,
    HomeAssistantMqttProjectionMapper projectionMapper,
    CancellationToken cancellationToken)
{
    var token = await tokenCoordinator.EnsureUsableAsync(state, cancellationToken);
    var snapshot = await dataClient.FetchSnapshotAsync(token.State, cancellationToken);
    var normalized = stateMapper.Map(snapshot, token.State.FleetApiAudience);
    var projection = projectionMapper.Map(normalized, token.State.MqttBaseTopic);
    var devices = projection.Devices.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
    var entities = projection.Entities
        .Select(entity =>
        {
            devices.TryGetValue(entity.DeviceId, out var device);
            return new HomeAssistantProjectionPreviewEntity(
                entity.Id,
                device?.Name ?? entity.DeviceId,
                entity.Component,
                entity.Name,
                entity.StateTopic,
                entity.CommandTopic,
                entity.ValueTemplate,
                entity.DeviceClass,
                entity.UnitOfMeasurement,
                entity.EnabledByDefault);
        })
        .OrderBy(entity => entity.DeviceName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var checks = token.Checks.Concat(snapshot.Checks).ToList();
    checks.Add($"Projection contains {projection.Devices.Count} device(s), {projection.Entities.Count} MQTT discoverable entit{(projection.Entities.Count == 1 ? "y" : "ies")}, and {projection.States.Count} state topic(s).");
    return new HomeAssistantProjectionPreviewRun(
        token.State,
        entities,
        checks,
        $"Previewed {entities.Count} Home Assistant MQTT entit{(entities.Count == 1 ? "y" : "ies")} from {projection.Devices.Count} device(s).");
}

static TeslaFleetState ApplyHomeAssistantProjectionPreview(HomeAssistantProjectionPreviewRun run, string status) =>
    run.State with
    {
        HomeAssistantProjectionPreviewEntities = run.Entities,
        LastHomeAssistantProjectionPreviewUtc = DateTimeOffset.UtcNow,
        LastHomeAssistantProjectionPreviewSummary = run.Summary,
        LastStatus = status,
        LastMessage = run.Summary,
        LastChecks = run.Checks
    };

static object BuildPropertyHarnessStatus(TeslaFleetState state)
{
    var properties = state.DiscoveredProperties ?? [];
    return new
    {
        configured = !string.IsNullOrWhiteSpace(state.RefreshToken),
        lastDiscoveryUtc = state.LastPropertyDiscoveryUtc,
        summary = string.IsNullOrWhiteSpace(state.LastPropertyDiscoverySummary)
            ? "No Tesla API property discovery has been run yet."
            : state.LastPropertyDiscoverySummary,
        propertyCount = properties.Count,
        vehiclePropertyCount = properties.Count(property => property.Scope.Equals("vehicle", StringComparison.OrdinalIgnoreCase)),
        energyPropertyCount = properties.Count(property => property.Scope.Equals("energy", StringComparison.OrdinalIgnoreCase)),
        userPropertyCount = properties.Count(property => property.Scope.Equals("user", StringComparison.OrdinalIgnoreCase)),
        regionPropertyCount = properties.Count(property => property.Scope.Equals("region", StringComparison.OrdinalIgnoreCase)),
        fetchRealtimeVehicleData = state.FetchRealtimeVehicleData,
        fleetApiAudience = state.FleetApiAudience,
        lastStatus = state.LastStatus,
        lastMessage = state.LastMessage
    };
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

static string RenderPage(TeslaFleetState state, EdgeGatewayCompanionStatus? companion = null)
{
    companion ??= EdgeGatewayCompanionStatus.Unknown();
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
    var virtualKeyActionClass = hasKey && isPartnerRegistered && !string.IsNullOrWhiteSpace(state.OriginDomain)
        ? string.Empty
        : "disabled";
    var companionStatusClass = companion.EdgeGatewayHealthy ? "ready" : companion.EdgeGatewayInstalled ? "warn" : "fail";
    var companionLabel = companion.EdgeGatewayHealthy
        ? "Auto-detected"
        : companion.EdgeGatewayInstalled ? "Installed, not reachable" : "Install Edge Gateway";
    var companionChecks = RenderCompanionChecks(companion);
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
          <form method="post" action="actions/check-companion-link"><button type="submit">Check Edge Gateway link</button></form>
          <form method="post" action="actions/test-public-key"><button type="submit">Check public key URL</button></form>
          <form method="post" action="actions/test-tesla-public-key"><button type="submit">Check Tesla public key</button></form>
          <form method="post" action="actions/refresh-token"><button type="submit">Refresh Tesla token</button></form>
          <form method="post" action="actions/list-vehicles"><button type="submit">Check Tesla vehicles API</button></form>
          <form method="post" action="actions/publish-ha"><button type="submit">Publish to Home Assistant</button></form>
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
    var discoveredProperties = state.DiscoveredProperties ?? [];
    var propertyRows = RenderPropertyRows(discoveredProperties);
    var propertyScopeOptions = RenderPropertyScopeOptions(discoveredProperties);
    var propertyResourceOptions = RenderPropertyResourceOptions(discoveredProperties);
    var propertySummary = string.IsNullOrWhiteSpace(state.LastPropertyDiscoverySummary)
        ? "No discovery run yet."
        : state.LastPropertyDiscoverySummary;
    var projectionPreviewEntities = state.HomeAssistantProjectionPreviewEntities ?? [];
    var projectionPreviewRows = RenderProjectionPreviewRows(projectionPreviewEntities);
    var projectionComponentOptions = RenderProjectionComponentOptions(projectionPreviewEntities);
    var projectionPreviewSummary = string.IsNullOrWhiteSpace(state.LastHomeAssistantProjectionPreviewSummary)
        ? "No Home Assistant projection preview has been run yet."
        : state.LastHomeAssistantProjectionPreviewSummary;
    var energyScopeWarning = HasScope(state.TeslaScopes, EnergyDeviceDataScope) &&
                             HasScope(state.TeslaScopes, EnergyCommandsScope)
        ? string.Empty
        : """
          <div class="callout warn" style="margin:10px 0 0">
            <strong>Reconnect Tesla OAuth for energy controls</strong>
            <p style="margin:8px 0 0">This setup needs Tesla Energy read and command scopes. Start Tesla OAuth again so live Powerwall/Gateway data can be read and writable Home Assistant controls can send commands.</p>
          </div>
        """;

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
    main { width: min(1540px, calc(100vw - 24px)); margin: 0 auto; padding: 28px 0 48px; }
    header { display: flex; justify-content: space-between; gap: 24px; align-items: flex-start; margin-bottom: 18px; }
    h1, h2, h3, p { margin-top: 0; }
    h1 { font-size: 28px; margin-bottom: 8px; }
    h2 { font-size: 18px; margin-bottom: 10px; }
    h3 { font-size: 15px; margin-bottom: 8px; }
    .meta { color: var(--muted); line-height: 1.5; max-width: 820px; }
    .grid { display: grid; grid-template-columns: 1.1fr .9fr; gap: 16px; align-items: start; }
    .cards { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 12px; margin: 16px 0; }
    .tab-control {
      display: grid;
      grid-template-rows: auto minmax(0, 1fr);
      min-height: 0;
      min-width: 0;
      margin-top: 16px;
    }
    .tab-control-bar {
      align-items: end;
      border-bottom: 1px solid var(--border);
      display: flex;
      flex-wrap: wrap;
      min-height: 36px;
    }
    .tab-list {
      align-items: flex-end;
      display: flex;
      flex: 1 1 auto;
      flex-wrap: wrap;
      gap: 4px;
      margin-bottom: -1px;
      min-width: 0;
      padding: 0;
    }
    .tab-trigger {
      align-items: center;
      background: #111925;
      border: 1px solid var(--border);
      border-bottom-color: rgba(79, 209, 197, .08);
      border-radius: 10px 10px 0 0;
      color: var(--muted);
      cursor: pointer;
      display: inline-flex;
      flex: 0 0 auto;
      gap: 8px;
      justify-content: center;
      min-height: 34px;
      min-width: 0;
      padding: 7px 13px 6px;
      position: relative;
      text-align: left;
    }
    .tab-trigger:hover { border-color: #526174; color: var(--text); }
    .tab-trigger.active {
      background: var(--surface);
      border-color: rgba(79, 209, 197, .42);
      border-bottom-color: transparent;
      color: var(--text);
      margin-bottom: -1px;
      z-index: 1;
    }
    .tab-trigger.active::after {
      background: var(--surface);
      bottom: -1px;
      content: "";
      height: 1px;
      left: -1px;
      position: absolute;
      right: -1px;
    }
    .tab-trigger-title {
      display: block;
      font-size: 13px;
      font-weight: 750;
      line-height: 1.2;
    }
    .tab-trigger.active .tab-trigger-title { font-weight: 850; }
    .page-tab-panel {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 0 10px 10px 10px;
      box-shadow: 0 16px 36px rgba(0, 0, 0, .2);
      margin-top: -1px;
      min-height: 0;
      min-width: 0;
      overflow: auto;
      padding: 16px;
      position: relative;
    }
    .tab-panel { display: none; }
    .tab-panel.active.grid { display: grid; }
    .tab-panel.active.card { display: block; }
    .tab-panel + .tab-panel { margin-top: 0; }
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
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
    .check-row {
      display: flex;
      grid-template-columns: none;
      align-items: center;
      gap: 10px;
      min-height: 34px;
      margin-bottom: 10px;
    }
    .check-row input { width: 18px; min-height: 18px; height: 18px; margin: 0; }
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
    .diagnostic-filter-bar {
      display: grid;
      grid-template-columns: repeat(4, minmax(150px, 1fr));
      gap: 10px;
      align-items: end;
      margin: 12px 0;
    }
    .diagnostic-filter-bar label {
      display: grid;
      gap: 5px;
      color: var(--muted);
      font-size: 12px;
      font-weight: 700;
    }
    .diagnostic-filter-bar select,
    .diagnostic-filter-bar input {
      min-height: 38px;
      border: 1px solid var(--border);
      border-radius: 7px;
      background: var(--surface-2);
      color: var(--text);
      padding: 8px 10px;
      font: inherit;
      font-size: 13px;
    }
    .diagnostic-count {
      color: var(--muted);
      font-size: 13px;
      margin: 0 0 10px;
    }
    .table-wrap {
      overflow: auto;
      max-height: 640px;
      border: 1px solid var(--border);
      border-radius: 8px;
      background: #0c1118;
    }
    .property-table { width: 100%; min-width: 1660px; border-collapse: collapse; table-layout: fixed; }
    .property-table th,
    .property-table td {
      padding: 10px 12px;
      border-bottom: 1px solid rgba(51, 65, 85, .72);
      text-align: left;
      vertical-align: middle;
      font-size: 13px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .property-table th {
      color: var(--muted);
      font-weight: 700;
      background: rgba(29, 39, 52, .94);
      position: sticky;
      top: 0;
      z-index: 1;
    }
    .property-table code {
      display: inline;
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--accent-2);
      overflow-wrap: normal;
      word-break: normal;
      white-space: nowrap;
    }
    .resource-main { display: block; overflow: hidden; text-overflow: ellipsis; color: var(--text); }
    .resource-id { display: block; overflow: hidden; text-overflow: ellipsis; color: var(--muted); font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; margin-top: 3px; }
    .value-cell { color: var(--text); font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
    .property-table td.value-cell {
      white-space: normal;
      overflow: visible;
      text-overflow: clip;
      line-height: 1.4;
    }
    .value-cell details { max-width: 100%; }
    .value-cell summary {
      cursor: pointer;
      color: var(--accent-2);
      white-space: normal;
      overflow-wrap: anywhere;
    }
    .value-cell pre {
      margin: 8px 0 0;
      padding: 10px;
      border: 1px solid var(--border);
      border-radius: 7px;
      max-height: 360px;
      overflow: auto;
      background: #05070a;
      color: var(--text);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .property-table .scope-col { width: 104px; }
    .property-table .resource-col { width: 250px; }
    .property-table .path-col { width: 380px; }
    .property-table .display-col { width: 180px; }
    .property-table .type-col { width: 96px; }
    .property-table .hint-col { width: 190px; }
    .property-table .value-col { width: 470px; }
    .pill {
      display: inline-flex;
      align-items: center;
      min-height: 24px;
      padding: 2px 8px;
      border-radius: 999px;
      border: 1px solid var(--border);
      color: var(--muted);
      white-space: nowrap;
    }
    @media (max-width: 900px) {
      main { width: min(100vw - 20px, 760px); padding-top: 18px; }
      header, .grid, .cards, .split-actions, .form-grid { grid-template-columns: 1fr; display: grid; }
      .diagnostic-filter-bar { grid-template-columns: 1fr; }
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
      <div class="card"><h3>Edge Gateway</h3><span class="status {{companionStatusClass}}">{{H(companionLabel)}}</span></div>
      <div class="card"><h3>Key</h3><span class="status {{(hasKey ? "ready" : "warn")}}">{{(hasKey ? "EC P-256 ready" : "Generate required")}}</span></div>
      <div class="card"><h3>Publish</h3><span class="status {{(!string.IsNullOrWhiteSpace(state.PublicAssetId?.ToString()) ? "ready" : "warn")}}">{{(!string.IsNullOrWhiteSpace(state.PublicAssetId?.ToString()) ? "Edge Gateway asset linked" : "Not published")}}</span></div>
      <div class="card"><h3>Tesla</h3><span class="status {{(isPartnerRegistered ? "ready" : "warn")}}">{{H(partnerStatus)}}</span></div>
      <div class="card"><h3>OAuth</h3><span class="status {{(hasOAuthToken ? "ready" : "warn")}}">{{H(tokenStatus)}}</span></div>
    </section>

    <section class="tab-control helper-tabs" data-helper-tabs>
      <div class="tab-control-bar">
        <div class="tab-list" role="tablist" aria-label="Tesla Fleet Helper sections">
          <button class="tab-trigger active" type="button" role="tab" aria-selected="true" data-helper-tab="setup"><span class="tab-trigger-title">Setup & Publish</span></button>
          <button class="tab-trigger" type="button" role="tab" aria-selected="false" data-helper-tab="diagnostics"><span class="tab-trigger-title">Diagnostics</span></button>
          <button class="tab-trigger" type="button" role="tab" aria-selected="false" data-helper-tab="harness"><span class="tab-trigger-title">Entity Harness</span></button>
        </div>
      </div>
      <div class="page-tab-panel">
    <section class="grid tab-panel active" data-helper-tab-panel="setup">
      <div class="card">
        <h2>Setup</h2>
        <form method="post" action="actions/save-settings">
          <div class="callout" style="margin:0 0 14px">
            <strong>LMS Edge Gateway companion</strong>
            <p style="margin:8px 0 0">{{H(companion.Summary)}}</p>
            <ul>{{companionChecks}}</ul>
          </div>
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
          {{energyScopeWarning}}
          <div class="callout" style="margin:14px 0">
            <strong>Home Assistant MQTT Discovery</strong>
            <p style="margin:8px 0 0">Publishes Tesla vehicles and energy sites as MQTT-discovered Home Assistant devices. Realtime vehicle data is optional and only fetched for online vehicles.</p>
          </div>
          <label class="check-row">
            <input type="hidden" name="ha_mqtt_enabled_present" value="true" />
            <input type="checkbox" name="ha_mqtt_enabled" {{(state.HomeAssistantMqttEnabled ? "checked" : "")}} />
            Enable Home Assistant MQTT publishing
          </label>
          <label class="check-row">
            <input type="hidden" name="fetch_realtime_vehicle_data_present" value="true" />
            <input type="checkbox" name="fetch_realtime_vehicle_data" {{(state.FetchRealtimeVehicleData ? "checked" : "")}} />
            Fetch realtime vehicle data for online vehicles
          </label>
          <div class="form-grid">
            <label>
              MQTT host
              <input name="mqtt_host" value="{{H(state.MqttHost)}}" autocomplete="off" />
            </label>
            <label>
              MQTT port
              <input name="mqtt_port" value="{{H(state.MqttPort.ToString())}}" inputmode="numeric" autocomplete="off" />
            </label>
          </div>
          <div class="form-grid">
            <label>
              MQTT username
              <input name="mqtt_username" value="{{H(state.MqttUsername)}}" autocomplete="off" />
            </label>
            <label>
              MQTT password
              <input name="mqtt_password" value="" placeholder="{{(string.IsNullOrWhiteSpace(state.MqttPassword) ? "Not saved" : "Saved - leave blank to keep")}}" autocomplete="off" />
            </label>
          </div>
          <div class="form-grid">
            <label>
              Discovery prefix
              <input name="mqtt_discovery_prefix" value="{{H(state.MqttDiscoveryPrefix)}}" autocomplete="off" />
            </label>
            <label>
              Base topic
              <input name="mqtt_base_topic" value="{{H(state.MqttBaseTopic)}}" autocomplete="off" />
            </label>
          </div>
          <label>
            Refresh interval minutes
            <input name="ha_refresh_interval_minutes" value="{{H(state.HomeAssistantRefreshIntervalMinutes.ToString())}}" inputmode="numeric" autocomplete="off" />
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
          <a class="button" href="{{H(oauthStartUrl)}}" target="_blank" rel="noopener noreferrer">Start Tesla OAuth</a>
          <a class="button {{virtualKeyActionClass}}" href="{{H(virtualKeyUrl)}}" target="_blank" rel="noopener noreferrer">Install virtual key</a>
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
          Install the virtual key only once per vehicle, or after rotating the Fleet key; OAuth reconnects do not require reinstalling it.
        </div>
      </div>
    </section>

    <section class="grid tab-panel" data-helper-tab-panel="diagnostics">
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
          <div><span>Last Home Assistant publish</span><code>{{FormatDate(state.LastHomeAssistantPublishUtc)}}</code></div>
          <div><span>Home Assistant publish summary</span><code>{{H(string.IsNullOrWhiteSpace(state.LastHomeAssistantPublishSummary) ? "None" : state.LastHomeAssistantPublishSummary)}}</code></div>
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

    <section class="card tab-panel" data-helper-tab-panel="harness">
      <h2>Tesla API Property Harness</h2>
      <p class="meta">Discover the fields Tesla returns before mapping them into Home Assistant. Values are sanitized, VINs are masked, and token-like fields are redacted.</p>
      <div class="fact-grid" style="margin:12px 0">
        <div><span>Last property discovery</span><code>{{FormatDate(state.LastPropertyDiscoveryUtc)}}</code></div>
        <div><span>Discovery summary</span><code>{{H(propertySummary)}}</code></div>
        <div><span>Cached property count</span><code>{{H(discoveredProperties.Count.ToString())}}</code></div>
        <div><span>Projection preview</span><code>{{H(projectionPreviewSummary)}}</code></div>
        <div><span>JSON endpoints</span><code>GET /api/test-harness/status | GET /api/test-harness/properties | GET /api/test-harness/ha-projection | POST /api/test-harness/discover | POST /api/test-harness/ha-projection/refresh</code></div>
      </div>
      <div class="split-actions" style="margin:12px 0">
        <form method="post" action="actions/discover-properties"><button class="primary" type="submit">Discover / refresh properties</button></form>
        <form method="post" action="actions/preview-ha-projection"><button type="submit">Preview HA MQTT projection</button></form>
        <form method="post" action="actions/clear-properties"><button type="submit">Clear cached properties</button></form>
      </div>
      <h3>Home Assistant Entity Projection</h3>
      <div class="diagnostic-filter-bar" data-projection-filters>
        <label>Component
          <select data-projection-component-filter>
            <option value="">All components</option>
            {{projectionComponentOptions}}
          </select>
        </label>
        <label>Command
          <select data-projection-command-filter>
            <option value="">All entities</option>
            <option value="writable">Writable only</option>
            <option value="readonly">Read-only only</option>
          </select>
        </label>
        <label>Search
          <input type="search" placeholder="Device, entity, topic..." data-projection-search />
        </label>
      </div>
      <p class="diagnostic-count" data-projection-count>Total projected entities: {{projectionPreviewEntities.Count}}</p>
      <div class="table-wrap" style="margin-bottom:12px">
        <table class="property-table">
          <colgroup>
            <col class="resource-col" />
            <col class="display-col" />
              <col class="type-col" />
              <col class="path-col" />
              <col class="path-col" />
              <col class="value-col" />
              <col class="hint-col" />
              <col class="scope-col" />
          </colgroup>
          <thead>
            <tr>
              <th>Device</th>
              <th>Entity</th>
              <th>Type</th>
              <th>State Topic</th>
              <th>Command Topic</th>
              <th>Template</th>
              <th>Class / Unit</th>
              <th>Enabled</th>
            </tr>
          </thead>
          <tbody>
            {{projectionPreviewRows}}
          </tbody>
        </table>
      </div>
      <h3>Discovered Tesla Properties</h3>
      <div class="diagnostic-filter-bar" data-property-filters>
        <label>Scope
          <select data-property-scope-filter>
            <option value="">All scopes</option>
            {{propertyScopeOptions}}
          </select>
        </label>
        <label>Resource
          <select data-property-resource-filter>
            <option value="">All resources</option>
            {{propertyResourceOptions}}
          </select>
        </label>
        <label>Value
          <select data-property-value-filter>
            <option value="">Any value state</option>
            <option value="valued">Has value</option>
            <option value="empty">Empty/null</option>
          </select>
        </label>
        <label>Search
          <input type="search" placeholder="Path, display, value..." data-property-search />
        </label>
      </div>
      <p class="diagnostic-count" data-property-count>Total discovered properties: {{discoveredProperties.Count}}</p>
      <div class="table-wrap">
        <table class="property-table">
          <colgroup>
            <col class="scope-col" />
            <col class="resource-col" />
            <col class="path-col" />
            <col class="display-col" />
            <col class="type-col" />
            <col class="hint-col" />
            <col class="value-col" />
          </colgroup>
          <thead>
            <tr>
              <th>Scope</th>
              <th>Resource</th>
              <th>Path</th>
              <th>Display</th>
              <th>Type</th>
              <th>Suggestion</th>
              <th>Value</th>
            </tr>
          </thead>
          <tbody>
            {{propertyRows}}
          </tbody>
        </table>
      </div>
    </section>
      </div>
    </section>
  </main>
  <script>
    (() => {
      const root = document.querySelector("[data-helper-tabs]");
      if (!root) return;
      const triggers = Array.from(root.querySelectorAll("[data-helper-tab]"));
      const panels = Array.from(root.querySelectorAll("[data-helper-tab-panel]"));
      const activate = tab => {
        for (const trigger of triggers) {
          const active = trigger.dataset.helperTab === tab;
          trigger.classList.toggle("active", active);
          trigger.setAttribute("aria-selected", active ? "true" : "false");
        }
        for (const panel of panels) {
          panel.classList.toggle("active", panel.dataset.helperTabPanel === tab);
        }
        try {
          window.localStorage.setItem("lms-tesla-helper-tab", tab);
        } catch {
        }
      };
      for (const trigger of triggers) {
        trigger.addEventListener("click", () => activate(trigger.dataset.helperTab));
      }
      try {
        const saved = window.localStorage.getItem("lms-tesla-helper-tab");
        if (saved && triggers.some(trigger => trigger.dataset.helperTab === saved)) {
          activate(saved);
        }
      } catch {
      }
    })();
    (() => {
      const wireFilters = config => {
        const rows = Array.from(document.querySelectorAll(config.rowSelector));
        if (rows.length === 0) return;
        const controls = config.controls.map(selector => document.querySelector(selector));
        const count = document.querySelector(config.countSelector);
        const normalize = value => (value || "").trim().toLowerCase();
        const apply = () => {
          let visible = 0;
          const values = controls.map(control => normalize(control?.value));
          for (const row of rows) {
            const show = config.match(row, values, normalize);
            row.style.display = show ? "" : "none";
            if (show) visible += 1;
          }
          if (count) count.textContent = `Showing ${visible} of ${rows.length} ${config.label}.`;
        };
        for (const control of controls) {
          if (!control) continue;
          control.addEventListener("change", apply);
          control.addEventListener("input", apply);
        }
        apply();
      };

      wireFilters({
        rowSelector: "[data-projection-row]",
        countSelector: "[data-projection-count]",
        label: "projected entities",
        controls: ["[data-projection-component-filter]", "[data-projection-command-filter]", "[data-projection-search]"],
        match: (row, values, normalize) => {
          const [component, commandState, search] = values;
          return (!component || normalize(row.dataset.component) === component) &&
                 (!commandState || normalize(row.dataset.commandState) === commandState) &&
                 (!search || normalize(row.dataset.search).includes(search));
        }
      });

      wireFilters({
        rowSelector: "[data-property-row]",
        countSelector: "[data-property-count]",
        label: "discovered properties",
        controls: ["[data-property-scope-filter]", "[data-property-resource-filter]", "[data-property-value-filter]", "[data-property-search]"],
        match: (row, values, normalize) => {
          const [scope, resource, valueState, search] = values;
          const hasValue = row.dataset.hasValue === "true";
          return (!scope || normalize(row.dataset.scope) === scope) &&
                 (!resource || normalize(row.dataset.resource) === resource) &&
                 (!valueState || (valueState === "valued" ? hasValue : !hasValue)) &&
                 (!search || normalize(row.dataset.search).includes(search));
        }
      });
    })();
  </script>
</body>
</html>
""";
}

static string H(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

static TeslaFleetState ApplyCompanionDefaults(TeslaFleetState state) =>
    state with
    {
        EdgeGatewayUrl = TeslaFleetDefaults.LocalEdgeGatewayUrl,
        PublicUpstreamUrl = TeslaFleetDefaults.LocalHelperUpstreamUrl
    };

static IResult RedirectToAppRoot() =>
    Results.Redirect("../");

static string RenderCompanionChecks(EdgeGatewayCompanionStatus companion)
{
    if (companion.Checks.Count == 0)
    {
        return "<li>No companion checks have run yet.</li>";
    }

    return string.Concat(companion.Checks.Select(check => $"<li>{H(check)}</li>"));
}

static string BuildStatusClass(string? status)
{
    var value = (status ?? string.Empty).ToLowerInvariant();
    if (value.Contains("verified", StringComparison.Ordinal) ||
        value.Contains("published", StringComparison.Ordinal) ||
        value.Contains("ready", StringComparison.Ordinal) ||
        value.Contains("saved", StringComparison.Ordinal) ||
        value.Contains("connected", StringComparison.Ordinal) ||
        value.Contains("healthy", StringComparison.Ordinal))
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

static string RenderPropertyScopeOptions(IReadOnlyList<TeslaDiscoveredProperty> properties) =>
    string.Concat(properties
        .Select(property => property.Scope)
        .Where(scope => !string.IsNullOrWhiteSpace(scope))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
        .Select(scope => $"""<option value="{H(scope)}">{H(scope)}</option>"""));

static string RenderPropertyResourceOptions(IReadOnlyList<TeslaDiscoveredProperty> properties) =>
    string.Concat(properties
        .Select(property => new
        {
            Key = $"{property.Scope}|{property.ResourceName}|{property.ResourceId}",
            Label = $"{property.Scope}: {property.ResourceName} {property.ResourceId}".Trim()
        })
        .Where(resource => !string.IsNullOrWhiteSpace(resource.Label))
        .DistinctBy(resource => resource.Key, StringComparer.OrdinalIgnoreCase)
        .OrderBy(resource => resource.Label, StringComparer.OrdinalIgnoreCase)
        .Select(resource => $"""<option value="{H(resource.Key)}">{H(resource.Label)}</option>"""));

static string RenderProjectionComponentOptions(IReadOnlyList<HomeAssistantProjectionPreviewEntity> entities) =>
    string.Concat(entities
        .Select(entity => entity.Component)
        .Where(component => !string.IsNullOrWhiteSpace(component))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(component => component, StringComparer.OrdinalIgnoreCase)
        .Select(component => $"""<option value="{H(component)}">{H(component)}</option>"""));

static string RenderPropertyRows(IReadOnlyList<TeslaDiscoveredProperty> properties)
{
    if (properties.Count == 0)
    {
        return """
            <tr><td colspan="7">No properties discovered yet.</td></tr>
        """;
    }

    return string.Concat(properties
        .Select(property =>
        {
            var suggestion = string.Join(" / ", new[]
                {
                    property.SuggestedEntityType,
                    property.SuggestedDeviceClass,
                    property.SuggestedUnit
                }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var resourceTitle = $"{property.ResourceName} {property.ResourceId}".Trim();
            var resourceKey = $"{property.Scope}|{property.ResourceName}|{property.ResourceId}";
            var hasValue = HasPropertyValue(property.Value);
            var searchText = string.Join(' ', property.Scope, property.ResourceName, property.ResourceId, property.Path, property.DisplayName, property.ValueType, suggestion, property.Value);
            return $"""
            <tr data-property-row data-scope="{H(property.Scope)}" data-resource="{H(resourceKey)}" data-has-value="{(hasValue ? "true" : "false")}" data-search="{H(searchText.ToLowerInvariant())}">
              <td title="{H(property.Scope)}"><span class="pill">{H(property.Scope)}</span></td>
              <td title="{H(resourceTitle)}"><span class="resource-main">{H(property.ResourceName)}</span><span class="resource-id">{H(property.ResourceId)}</span></td>
              <td title="{H(property.Path)}"><code>{H(property.Path)}</code></td>
              <td title="{H(property.DisplayName)}">{H(property.DisplayName)}</td>
              <td title="{H(property.ValueType)}">{H(property.ValueType)}</td>
              <td title="{H(suggestion)}">{H(suggestion)}</td>
              <td class="value-cell">{RenderValueCell(property.Value)}</td>
            </tr>
""";
        }));
}

static string RenderProjectionPreviewRows(IReadOnlyList<HomeAssistantProjectionPreviewEntity> entities)
{
    if (entities.Count == 0)
    {
        return """
            <tr><td colspan="8">No Home Assistant MQTT projection preview has been run yet.</td></tr>
        """;
    }

    return string.Concat(entities
        .Select(entity =>
        {
            var hint = string.Join(" / ", new[]
                {
                    entity.DeviceClass,
                    entity.UnitOfMeasurement
                }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var enabled = entity.EnabledByDefault ? "Yes" : "No";
            var commandState = string.IsNullOrWhiteSpace(entity.CommandTopic) ? "readonly" : "writable";
            var searchText = string.Join(' ', entity.DeviceName, entity.Id, entity.Name, entity.Component, entity.StateTopic, entity.CommandTopic, entity.ValueTemplate, hint, enabled);
            return $"""
            <tr data-projection-row data-component="{H(entity.Component)}" data-command-state="{commandState}" data-search="{H(searchText.ToLowerInvariant())}">
              <td title="{H(entity.DeviceName)}"><span class="resource-main">{H(entity.DeviceName)}</span><span class="resource-id">{H(entity.Id)}</span></td>
              <td title="{H(entity.Name)}">{H(entity.Name)}</td>
              <td title="{H(entity.Component)}">{H(entity.Component)}</td>
              <td title="{H(entity.StateTopic)}"><code>{H(entity.StateTopic)}</code></td>
              <td title="{H(entity.CommandTopic ?? "Read only")}"><code>{H(string.IsNullOrWhiteSpace(entity.CommandTopic) ? "Read only" : entity.CommandTopic)}</code></td>
              <td class="value-cell" title="{H(entity.ValueTemplate)}">{H(TruncateForUi(entity.ValueTemplate, 260))}</td>
              <td title="{H(hint)}">{H(hint)}</td>
              <td title="{H(enabled)}"><span class="pill">{H(enabled)}</span></td>
            </tr>
""";
        }));
}

static bool HasPropertyValue(string value) =>
    !string.IsNullOrWhiteSpace(value) &&
    !value.Equals("null", StringComparison.OrdinalIgnoreCase);

static string RenderValueCell(string value)
{
    if (!HasPropertyValue(value))
    {
        return """<span class="pill">empty</span>""";
    }

    var preview = TruncateForUi(value.ReplaceLineEndings(" "), 220);
    return $"""
<details>
  <summary>{H(preview)}</summary>
  <pre>{H(value)}</pre>
</details>
""";
}

static string TruncateForUi(string value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
    {
        return value;
    }

    return $"{value[..maxLength]}...";
}

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
        ["prompt_missing_scopes"] = "true"
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

    if (!scopes.Contains(EnergyDeviceDataScope, StringComparer.Ordinal))
    {
        scopes = [.. scopes, EnergyDeviceDataScope];
    }

    if (!scopes.Contains(EnergyCommandsScope, StringComparer.Ordinal))
    {
        scopes = [.. scopes, EnergyCommandsScope];
    }

    return string.Join(' ', scopes);
}

static bool HasScope(string value, string scope) =>
    (value ?? string.Empty)
        .Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains(scope, StringComparer.Ordinal);

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
    <p>No virtual-key install was started. Return to the Tesla Fleet Helper tab in Home Assistant.</p>
    <p>Use the separate Install virtual key action only for first setup, a new vehicle, or after rotating the Fleet key.</p>
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
    <p>Return to the Tesla Fleet Helper tab in Home Assistant to continue.</p>
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

static string NormalizeHttpUrl(string value, string defaultUrl = TeslaFleetDefaults.LocalEdgeGatewayUrl)
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

static bool IsChecked(string value) =>
    value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
    value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
    value.Equals("1", StringComparison.OrdinalIgnoreCase);

static string NormalizeHost(string value, string defaultValue)
{
    var trimmed = (value ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return defaultValue;
    }

    if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("Enter a valid MQTT host.");
        }

        trimmed = uri.Host;
    }

    if (trimmed.Contains('/', StringComparison.Ordinal) ||
        trimmed.Contains('\\', StringComparison.Ordinal) ||
        trimmed.Contains(' ', StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Enter a valid MQTT host name or IP address.");
    }

    return trimmed;
}

static int NormalizePort(string value, int defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value.Trim(), out var port) || port < 1 || port > 65535)
    {
        throw new InvalidOperationException("Enter a valid MQTT port between 1 and 65535.");
    }

    return port;
}

static int NormalizeInt(string value, int defaultValue, int min, int max)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value.Trim(), out var parsed))
    {
        throw new InvalidOperationException($"Enter a number between {min} and {max}.");
    }

    return Math.Clamp(parsed, min, max);
}

static string NormalizeTopicRoot(string value, string defaultValue)
{
    var trimmed = (value ?? string.Empty).Trim().Trim('/');
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return defaultValue;
    }

    if (trimmed.Contains('#', StringComparison.Ordinal) ||
        trimmed.Contains('+', StringComparison.Ordinal) ||
        trimmed.Contains('\\', StringComparison.Ordinal))
    {
        throw new InvalidOperationException("MQTT topics cannot contain wildcards or backslashes.");
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

    public string PrivateKeyPath => Path.Combine(dataRoot, "secrets", "tesla_fleet.key");

    public string VehicleCommandProxyTlsCertPath => Path.Combine(dataRoot, "vehicle-command-proxy", "tls-cert.pem");

    public string VehicleCommandProxyTlsKeyPath => Path.Combine(dataRoot, "vehicle-command-proxy", "tls-key.pem");

    public string VehicleCommandProxyCachePath => Path.Combine(dataRoot, "vehicle-command-proxy", "cache.json");

    private string StatePath => Path.Combine(dataRoot, "state.json");

    public async Task<TeslaFleetState> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataRoot);
        if (!File.Exists(StatePath))
        {
            return ReadDefaultState();
        }

        await using var stream = File.OpenRead(StatePath);
        var state = await JsonSerializer.DeserializeAsync<TeslaFleetState>(stream, JsonOptions, cancellationToken) ??
                    new TeslaFleetState();
        return state with
        {
            EdgeGatewayUrl = TeslaFleetDefaults.LocalEdgeGatewayUrl,
            PublicUpstreamUrl = TeslaFleetDefaults.LocalHelperUpstreamUrl,
            FleetApiAudience = string.IsNullOrWhiteSpace(state.FleetApiAudience)
                ? TeslaFleetDefaults.DefaultFleetApiAudience
                : TeslaFleetDefaults.ResolveFleetApiAudience(state.FleetApiAudience, state.AccessToken),
            TeslaScopes = string.IsNullOrWhiteSpace(state.TeslaScopes)
                ? "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds energy_device_data energy_cmds"
                : NormalizeStoredScopes(state.TeslaScopes),
            PublicKeyUrl = string.IsNullOrWhiteSpace(state.PublicKeyUrl) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildPublicKeyUrl(state.OriginDomain)
                : state.PublicKeyUrl,
            OAuthStartUrl = string.IsNullOrWhiteSpace(state.OAuthStartUrl) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildOAuthStartUrl(state.OriginDomain)
                : state.OAuthStartUrl,
            OAuthRedirectUri = ShouldRefreshOAuthRedirectUri(state.OAuthRedirectUri) && !string.IsNullOrWhiteSpace(state.OriginDomain)
                ? TeslaFleetDefaults.BuildOAuthRedirectUri(state.OriginDomain)
                : state.OAuthRedirectUri,
            MqttHost = string.IsNullOrWhiteSpace(state.MqttHost) ? "core-mosquitto" : state.MqttHost,
            MqttPort = state.MqttPort <= 0 ? 1883 : state.MqttPort,
            MqttDiscoveryPrefix = string.IsNullOrWhiteSpace(state.MqttDiscoveryPrefix) ? "homeassistant" : state.MqttDiscoveryPrefix,
            MqttBaseTopic = string.IsNullOrWhiteSpace(state.MqttBaseTopic) ? "lms/tesla-fleet" : state.MqttBaseTopic,
            HomeAssistantRefreshIntervalMinutes = state.HomeAssistantRefreshIntervalMinutes <= 0
                ? 15
                : Math.Clamp(state.HomeAssistantRefreshIntervalMinutes, 5, 240),
            DiscoveredProperties = state.DiscoveredProperties ?? [],
            HomeAssistantProjectionPreviewEntities = state.HomeAssistantProjectionPreviewEntities ?? [],
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

    private TeslaFleetState ReadDefaultState() =>
        new(
            EdgeGatewayUrl: TeslaFleetDefaults.LocalEdgeGatewayUrl,
            PublicUpstreamUrl: TeslaFleetDefaults.LocalHelperUpstreamUrl,
            HomeAssistantMqttEnabled: ReadOptionBool("homeassistant_mqtt_enabled", false),
            FetchRealtimeVehicleData: ReadOptionBool("fetch_realtime_vehicle_data", false),
            MqttHost: ReadOptionString("mqtt_host", "core-mosquitto"),
            MqttPort: ReadOptionInt("mqtt_port", 1883),
            MqttUsername: ReadOptionString("mqtt_username", string.Empty),
            MqttPassword: ReadOptionString("mqtt_password", string.Empty),
            MqttDiscoveryPrefix: ReadOptionString("mqtt_discovery_prefix", "homeassistant"),
            MqttBaseTopic: ReadOptionString("mqtt_base_topic", "lms/tesla-fleet"),
            HomeAssistantRefreshIntervalMinutes: Math.Clamp(ReadOptionInt("homeassistant_refresh_interval_minutes", 15), 5, 240));

    private static string NormalizeStoredScopes(string value)
    {
        var scopes = (value ?? string.Empty)
            .Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scopes.Length == 0)
        {
            return "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds energy_device_data energy_cmds";
        }

        if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            scopes = ["openid", .. scopes];
        }

        if (!scopes.Contains("offline_access", StringComparer.Ordinal))
        {
            scopes = [.. scopes, "offline_access"];
        }

        if (!scopes.Contains("energy_device_data", StringComparer.Ordinal))
        {
            scopes = [.. scopes, "energy_device_data"];
        }

        if (!scopes.Contains("energy_cmds", StringComparer.Ordinal))
        {
            scopes = [.. scopes, "energy_cmds"];
        }

        return string.Join(' ', scopes);
    }

    private string ReadOptionString(string name, string fallback)
    {
        try
        {
            if (File.Exists(optionsJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(optionsJsonPath));
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!;
                }
            }
        }
        catch
        {
            // Fall back to the compiled default.
        }

        return fallback;
    }

    private int ReadOptionInt(string name, int fallback)
    {
        try
        {
            if (File.Exists(optionsJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(optionsJsonPath));
                if (document.RootElement.TryGetProperty(name, out var value))
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                    {
                        return number;
                    }

                    if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                    {
                        return number;
                    }
                }
            }
        }
        catch
        {
            // Fall back to the compiled default.
        }

        return fallback;
    }

    private bool ReadOptionBool(string name, bool fallback)
    {
        try
        {
            if (File.Exists(optionsJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(optionsJsonPath));
                if (document.RootElement.TryGetProperty(name, out var value))
                {
                    if (value.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }

                    if (value.ValueKind == JsonValueKind.False)
                    {
                        return false;
                    }

                    if (value.ValueKind == JsonValueKind.String &&
                        bool.TryParse(value.GetString(), out var boolean))
                    {
                        return boolean;
                    }
                }
            }
        }
        catch
        {
            // Fall back to the compiled default.
        }

        return fallback;
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

sealed class EdgeGatewayCompanionResolver(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<EdgeGatewayCompanionStatus> ResolveAsync(CancellationToken cancellationToken)
    {
        var checks = new List<string>();
        var supervisorAvailable = false;
        var installed = false;
        var started = false;
        var slug = string.Empty;
        var version = string.Empty;

        var supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        if (string.IsNullOrWhiteSpace(supervisorToken))
        {
            checks.Add("Home Assistant Supervisor token is not available in this runtime; using local Edge Gateway health detection.");
        }
        else
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "http://supervisor/addons");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", supervisorToken);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                supervisorAvailable = response.IsSuccessStatusCode;
                checks.Add($"Supervisor add-on lookup returned HTTP {(int)response.StatusCode}.");
                if (response.IsSuccessStatusCode)
                {
                    (installed, started, slug, version) = ReadEdgeGatewayAddon(body);
                    if (installed)
                    {
                        checks.Add(string.IsNullOrWhiteSpace(version)
                            ? $"Detected LMS Edge Gateway add-on {slug}."
                            : $"Detected LMS Edge Gateway add-on {slug} version {version}.");
                        checks.Add(started
                            ? "LMS Edge Gateway add-on is started."
                            : "LMS Edge Gateway add-on is installed but not started.");
                    }
                    else
                    {
                        checks.Add("LMS Edge Gateway add-on is not installed in this Supervisor instance.");
                    }
                }
                else
                {
                    checks.Add($"Supervisor response: {TruncateForDiagnostics(body)}");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                checks.Add($"Supervisor add-on lookup failed: {exception.Message}");
            }
        }

        var edgeGatewayHealthy = await CheckEdgeGatewayHealthAsync(checks, cancellationToken);
        if (edgeGatewayHealthy && !installed && !supervisorAvailable)
        {
            installed = true;
            started = true;
            slug = "lms_edge_gateway";
        }

        var summary = edgeGatewayHealthy
            ? "LMS Edge Gateway was auto-detected on this Home Assistant host."
            : installed
                ? "LMS Edge Gateway is installed, but the helper cannot reach it yet. Start or update the Edge Gateway add-on."
                : "Install and start LMS Edge Gateway on this Home Assistant host before publishing Tesla Fleet routes.";

        return new EdgeGatewayCompanionStatus(
            supervisorAvailable,
            installed,
            started,
            edgeGatewayHealthy,
            slug,
            version,
            summary,
            checks);
    }

    private async Task<bool> CheckEdgeGatewayHealthAsync(List<string> checks, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"{TeslaFleetDefaults.LocalEdgeGatewayUrl}/healthz",
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            checks.Add($"Local Edge Gateway health returned HTTP {(int)response.StatusCode}.");
            if (!response.IsSuccessStatusCode)
            {
                checks.Add($"Edge Gateway health response: {TruncateForDiagnostics(body)}");
                return false;
            }

            return body.Contains("Edge Gateway", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            checks.Add($"Local Edge Gateway health check failed: {exception.Message}");
            return false;
        }
    }

    private static (bool Installed, bool Started, string Slug, string Version) ReadEdgeGatewayAddon(string body)
    {
        using var document = JsonDocument.Parse(body);
        foreach (var addon in EnumerateAddons(document.RootElement))
        {
            var slug = ReadString(addon, "slug");
            var name = ReadString(addon, "name");
            if (!IsEdgeGatewayAddon(slug, name))
            {
                continue;
            }

            return (
                ReadBool(addon, "installed", defaultValue: true),
                ReadString(addon, "state").Equals("started", StringComparison.OrdinalIgnoreCase),
                slug,
                ReadString(addon, "version"));
        }

        return (false, false, string.Empty, string.Empty);
    }

    private static IEnumerable<JsonElement> EnumerateAddons(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("addons", out var dataAddons) &&
                dataAddons.ValueKind == JsonValueKind.Array)
            {
                return dataAddons.EnumerateArray();
            }

            if (data.ValueKind == JsonValueKind.Array)
            {
                return data.EnumerateArray();
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("addons", out var addons) &&
            addons.ValueKind == JsonValueKind.Array)
        {
            return addons.EnumerateArray();
        }

        return [];
    }

    private static bool IsEdgeGatewayAddon(string slug, string name) =>
        slug.Equals("lms_edge_gateway", StringComparison.OrdinalIgnoreCase) ||
        slug.EndsWith("_lms_edge_gateway", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LMS Edge Gateway for Home Assistant", StringComparison.OrdinalIgnoreCase);

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool ReadBool(JsonElement element, string propertyName, bool defaultValue)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return defaultValue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : defaultValue,
            _ => defaultValue
        };
    }

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";
}

sealed class EdgeGatewayPublicAssetClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TeslaCompanionLinkResult> CheckCompanionLinkAsync(
        string edgeGatewayUrl,
        string upstreamUrl,
        CancellationToken cancellationToken)
    {
        var checks = new List<string>();
        var normalizedEdgeGatewayUrl = TeslaFleetDefaults.NormalizeHttpUrl(edgeGatewayUrl);
        var normalizedUpstreamUrl = TeslaFleetDefaults.NormalizeHttpUrl(upstreamUrl, TeslaFleetDefaults.LocalHelperUpstreamUrl);

        using var edgeHealth = await httpClient.GetAsync(
            $"{normalizedEdgeGatewayUrl}/healthz",
            cancellationToken);
        var edgeHealthBody = await edgeHealth.Content.ReadAsStringAsync(cancellationToken);
        checks.Add($"Helper -> Edge Gateway health returned HTTP {(int)edgeHealth.StatusCode}.");
        if (!edgeHealth.IsSuccessStatusCode)
        {
            checks.Add($"Edge Gateway health response: {TruncateForDiagnostics(edgeHealthBody)}");
            return new TeslaCompanionLinkResult(
                false,
                $"Helper could not reach LMS Edge Gateway at {normalizedEdgeGatewayUrl}.",
                checks);
        }

        var payload = new PublicProxyUpstreamTestRequest(normalizedUpstreamUrl);
        using var upstreamResponse = await httpClient.PostAsJsonAsync(
            $"{normalizedEdgeGatewayUrl}/api/public-routes/test-upstream",
            payload,
            JsonOptions,
            cancellationToken);
        var upstreamBody = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
        var upstreamResult = Deserialize<PublicProxyUpstreamTestResponse>(upstreamBody);
        if (upstreamResult is not null)
        {
            checks.Add(upstreamResult.Summary);
            if (!string.IsNullOrWhiteSpace(upstreamResult.ResponsePreview))
            {
                checks.Add($"Helper health response: {TruncateForDiagnostics(upstreamResult.ResponsePreview)}");
            }

            return new TeslaCompanionLinkResult(
                upstreamResult.Succeeded,
                upstreamResult.Succeeded
                    ? "LMS Tesla Fleet Helper and LMS Edge Gateway can reach each other on this Home Assistant host."
                    : upstreamResult.Summary,
                checks);
        }

        checks.Add($"Edge Gateway upstream test returned HTTP {(int)upstreamResponse.StatusCode}: {TruncateForDiagnostics(upstreamBody)}");
        return new TeslaCompanionLinkResult(
            false,
            "Edge Gateway upstream health test failed.",
            checks);
    }

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

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";
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
                ? "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds energy_device_data energy_cmds"
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
            ["audience"] = string.IsNullOrWhiteSpace(audience) ? TeslaFleetDefaults.DefaultFleetApiAudience : audience.Trim()
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
            ["scope"] = "openid vehicle_device_data vehicle_cmds vehicle_charging_cmds energy_device_data energy_cmds"
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
        checks.AddRange(vehicles);

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

sealed record TeslaCompanionLinkResult(
    bool Succeeded,
    string Summary,
    List<string> Checks);

sealed record EdgeGatewayCompanionStatus(
    bool SupervisorAvailable,
    bool EdgeGatewayInstalled,
    bool EdgeGatewayStarted,
    bool EdgeGatewayHealthy,
    string Slug,
    string Version,
    string Summary,
    List<string> Checks)
{
    public static EdgeGatewayCompanionStatus Unknown() =>
        new(
            false,
            false,
            false,
            false,
            string.Empty,
            string.Empty,
            "LMS Edge Gateway companion status has not been checked yet.",
            []);
}

sealed record TeslaFleetState(
    string EdgeGatewayUrl = TeslaFleetDefaults.LocalEdgeGatewayUrl,
    string PublicUpstreamUrl = TeslaFleetDefaults.LocalHelperUpstreamUrl,
    string OriginDomain = "",
    string TeslaClientId = "",
    string TeslaClientSecret = "",
    string FleetApiAudience = "https://fleet-api.prd.na.vn.cloud.tesla.com",
    string TeslaScopes = "openid offline_access user_data vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds energy_device_data energy_cmds",
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
    bool HomeAssistantMqttEnabled = false,
    bool FetchRealtimeVehicleData = false,
    string MqttHost = "core-mosquitto",
    int MqttPort = 1883,
    string MqttUsername = "",
    string MqttPassword = "",
    string MqttDiscoveryPrefix = "homeassistant",
    string MqttBaseTopic = "lms/tesla-fleet",
    int HomeAssistantRefreshIntervalMinutes = 15,
    DateTimeOffset? LastHomeAssistantPublishUtc = null,
    string LastHomeAssistantPublishSummary = "",
    DateTimeOffset? LastPropertyDiscoveryUtc = null,
    string LastPropertyDiscoverySummary = "",
    List<TeslaDiscoveredProperty>? DiscoveredProperties = null,
    DateTimeOffset? LastHomeAssistantProjectionPreviewUtc = null,
    string LastHomeAssistantProjectionPreviewSummary = "",
    List<HomeAssistantProjectionPreviewEntity>? HomeAssistantProjectionPreviewEntities = null,
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

sealed record PublicProxyUpstreamTestRequest(
    string UpstreamUrl);

sealed record PublicProxyUpstreamTestResponse(
    bool Succeeded,
    string Summary,
    string UpstreamUrl,
    string HealthUrl,
    int? StatusCode,
    string ResponsePreview);

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
    public const string LocalEdgeGatewayUrl = "http://127.0.0.1:5000";
    public const string LocalHelperUpstreamUrl = "http://127.0.0.1:5055";
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

    public static string NormalizeHttpUrl(string value, string defaultUrl = LocalEdgeGatewayUrl)
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
