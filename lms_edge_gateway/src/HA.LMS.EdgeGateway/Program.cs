using HA.LMS.EdgeGateway.Components;
using HA.LMS.EdgeGateway.Services;
using LMS.EdgeGateway.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var edgeGatewayOptions = builder.Configuration.GetSection("EdgeGateway").Get<EdgeGatewayCoreOptions>() ?? new EdgeGatewayCoreOptions();

builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://0.0.0.0:5000");

builder.Services.Configure<EdgeGatewayCoreOptions>(builder.Configuration.GetSection("EdgeGateway"));
builder.Services.AddDataProtection()
    .SetApplicationName("HA.LMS.EdgeGateway")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(ResolvePath(edgeGatewayOptions.DataRoot), "data-protection")));
builder.Services.AddHttpContextAccessor();
builder.Services.AddEdgeGatewayCore();
builder.Services.AddHostedService<EdgeGatewayCaddyStartupService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<PasskeyAuthenticationService>();
builder.Services.AddScoped<LoginEmailOtpService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lms-edge-auth";
        options.LoginPath = "/login";
        options.LogoutPath = "/lmshaauth/logout";
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", product = "Linux Made Sane - Edge Gateway Add-on" }));
app.MapGet("/api/public-assets", async Task<IResult> (
    HttpContext context,
    IWellKnownServiceManager wellKnownManager,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedLocalPublicAssetRequest(context))
    {
        return Results.NotFound();
    }

    var configuration = await wellKnownManager.GetConfigurationAsync(cancellationToken);
    return Results.Json(configuration.Services.Select(MapPublicAsset));
}).AllowAnonymous();

app.MapPost("/api/public-assets/publish", async Task<IResult> (
    HttpContext context,
    IWellKnownServiceManager wellKnownManager,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedLocalPublicAssetRequest(context))
    {
        return Results.NotFound();
    }

    var request = await context.Request.ReadFromJsonAsync<PublicAssetPublishRequest>(
        cancellationToken: cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { succeeded = false, summary = "Enter a public asset payload." });
    }

    var hostname = WellKnownPath.NormalizeDomain(request.Hostname);
    var path = WellKnownPath.NormalizeRelativePath(request.Path);
    var configuration = await wellKnownManager.GetConfigurationAsync(cancellationToken);
    var existing = configuration.Services.FirstOrDefault(service =>
        service.Domain.Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
        service.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
    var sourceType = IsJsonPublicAsset(request.ContentType, path)
        ? WellKnownSourceType.Json
        : WellKnownSourceType.StaticText;
    var result = await wellKnownManager.SaveAsync(
        new WellKnownServiceSaveRequest(
            existing?.Id,
            string.IsNullOrWhiteSpace(request.Description) ? $"{hostname}{path}" : request.Description.Trim(),
            hostname,
            path,
            request.ContentType,
            request.Content,
            sourceType,
            Enabled: true,
            RequiresAuth: false,
            PublicReadOnly: true,
            CacheControl: string.IsNullOrWhiteSpace(request.CacheControl) ? "no-store" : request.CacheControl.Trim()),
        cancellationToken);

    return Results.Json(
        new
        {
            succeeded = result.Success,
            summary = result.Summary,
            warnings = result.Warnings,
            asset = result.Service is null ? null : MapPublicAsset(result.Service)
        },
        statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
}).AllowAnonymous().DisableAntiforgery();

app.MapDelete("/api/public-assets/{id:guid}", async Task<IResult> (
    Guid id,
    HttpContext context,
    IWellKnownServiceManager wellKnownManager,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedLocalPublicAssetRequest(context))
    {
        return Results.NotFound();
    }

    var result = await wellKnownManager.DeleteAsync(id, cancellationToken);
    return Results.Json(
        new
        {
            succeeded = result.Success,
            summary = result.Summary,
            warnings = result.Warnings
        },
        statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
}).AllowAnonymous().DisableAntiforgery();

app.MapPost("/api/public-assets/{id:guid}/verify", async Task<IResult> (
    Guid id,
    HttpContext context,
    IWellKnownServiceManager wellKnownManager,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedLocalPublicAssetRequest(context))
    {
        return Results.NotFound();
    }

    var result = await wellKnownManager.VerifyAsync(id, cancellationToken);
    return Results.Json(
        new
        {
            succeeded = result.Success,
            result.Status,
            result.Message,
            result.Checks,
            result.CheckedUtc
        },
        statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/edge-well-known/{serviceId:guid}", async Task<IResult> (
    Guid serviceId,
    HttpContext context,
    IWellKnownServiceStore store,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(context.Request.Headers["X-LMS-Well-Known-Proxy"].ToString(), "1", StringComparison.Ordinal))
    {
        return Results.NotFound();
    }

    var configuration = await store.LoadAsync(cancellationToken);
    var service = configuration.Services.FirstOrDefault(candidate => candidate.Id == serviceId && candidate.Enabled);
    if (service is null)
    {
        return Results.NotFound();
    }

    var forwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString();
    var requestedHost = NormalizeForwardedHost(string.IsNullOrWhiteSpace(forwardedHost)
        ? context.Request.Host.Host
        : forwardedHost);
    if (!requestedHost.Equals(service.Domain, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = string.IsNullOrWhiteSpace(service.CacheControl)
        ? "no-store"
        : service.CacheControl.Trim();
    context.Response.Headers["X-LMS-Well-Known-Service"] = service.Id.ToString("N");

    return Results.Bytes(
        Encoding.UTF8.GetBytes(service.Body ?? string.Empty),
        string.IsNullOrWhiteSpace(service.ContentType) ? "text/plain; charset=utf-8" : service.ContentType.Trim());
}).AllowAnonymous();

app.MapPost("/api/setup/cloudflare-api-token", async (
    HttpContext httpContext,
    ICloudflareApiTokenStore tokenStore,
    ICloudflareApiTokenValidator tokenValidator,
    CancellationToken cancellationToken) =>
{
    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    var token = form["cloudflare_api_token"].ToString().Trim();

    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Redirect("/?setup=missing");
    }

    var validation = await tokenValidator.ValidateAsync(token, cancellationToken);
    if (!validation.IsValid)
    {
        return Results.Redirect("/?setup=invalid");
    }

    await tokenStore.SaveTokenAsync(token, cancellationToken);
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapPost("/api/setup/reset-cloudflare-token", async (
    ICloudflareApiTokenStore tokenStore,
    CancellationToken cancellationToken) =>
{
    await tokenStore.ClearTokenAsync(cancellationToken);
    return Results.Redirect("/cloudflare");
}).DisableAntiforgery();

app.MapPost("/lmshaauth/email-otp", async (
    HttpContext context,
    LoginEmailOtpService emailOtpService,
    CancellationToken cancellationToken) =>
{
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var email = form["email"].ToString();
    var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
    var result = await emailOtpService.SendAsync(email, cancellationToken);

    return Results.Redirect(BuildLoginRedirectTarget(
        returnUrl,
        result.Succeeded ? null : result.Message,
        email,
        result.Succeeded ? result.Message : null,
        "email"));
}).DisableAntiforgery();

app.MapPost("/lmshaauth/login", async (
    HttpContext context,
    IEdgeGatewaySecurityService securityService,
    LoginEmailOtpService emailOtpService,
    CancellationToken cancellationToken) =>
{
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var email = form["email"].ToString();
    var authMethod = NormalizeAuthMethod(form["authMethod"].ToString());
    var authenticatorCode = form["authenticatorCode"].ToString();
    var emailCode = form["emailCode"].ToString();
    var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
    var result = SecurityAuthenticationResult.Failure("Enter your MFA code.");
    var mfaMethod = authMethod;

    if (authMethod == "email" || !string.IsNullOrWhiteSpace(emailCode))
    {
        result = await emailOtpService.ValidateAsync(email, emailCode, cancellationToken);
        mfaMethod = "email_otp";
    }

    if (!result.Succeeded &&
        (authMethod != "email" || !string.IsNullOrWhiteSpace(authenticatorCode)))
    {
        var authenticatorResult = await securityService.ValidateOtpAsync(email, authenticatorCode, cancellationToken);
        if (authenticatorResult.Succeeded || string.IsNullOrWhiteSpace(emailCode))
        {
            result = authenticatorResult;
            mfaMethod = "authenticator";
        }
    }

    if (!result.Succeeded || !result.UserId.HasValue || string.IsNullOrWhiteSpace(result.Email))
    {
        return Results.Redirect(BuildLoginRedirectTarget(returnUrl, result.Message, email, method: authMethod));
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, result.UserId.Value.ToString("D")),
        new(ClaimTypes.Name, result.Email),
        new(ClaimTypes.Email, result.Email),
        new("amr", "otp"),
        new("lms:mfa_method", mfaMethod),
        new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(
        SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.SessionLifetimeMinutes));

    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = expiresUtc
        });

    if (IsTruthy(form["enrollPasskey"].ToString()))
    {
        return Results.Redirect(BuildLoginRedirectTarget(
            returnUrl,
            null,
            result.Email,
            "Signed in. Save a passkey on this device, or skip for now.",
            "passkey",
            passkeySetup: true));
    }

    return Results.Redirect(returnUrl);
}).DisableAntiforgery();

app.MapPost("/lmshaauth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapGet("/edge-auth/check", async Task (
    HttpContext context,
    IEdgeGatewayRouteAuthService routeAuthService) =>
{
    var result = await routeAuthService.EvaluateAuthAsync(
        new EdgeGatewayAuthCheckContext(
            context.Request.Headers["X-Forwarded-Host"].ToString(),
            context.Request.Headers["X-Forwarded-Proto"].ToString(),
            context.Request.Headers["X-Forwarded-Uri"].ToString(),
            context.Request.Headers["X-Forwarded-For"].ToString(),
            context.Request.Headers.Host.ToString(),
            context.Connection.RemoteIpAddress,
            context.User),
        context.RequestAborted);

    context.Response.StatusCode = result.StatusCode;
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";

    if (!string.IsNullOrWhiteSpace(result.RedirectLocation))
    {
        context.Response.Headers.Location = result.RedirectLocation;
    }

    if (result.StatusCode == StatusCodes.Status200OK)
    {
        if (!string.IsNullOrWhiteSpace(result.UserName))
        {
            context.Response.Headers["X-LMS-User"] = result.UserName;
        }

        if (!string.IsNullOrWhiteSpace(result.UserEmail))
        {
            context.Response.Headers["X-LMS-Email"] = result.UserEmail;
        }

        if (!string.IsNullOrWhiteSpace(result.Groups))
        {
            context.Response.Headers["X-LMS-Groups"] = result.Groups;
        }
    }
}).DisableAntiforgery();

app.MapGet("/edge-auth/return", async (
    string? target,
    IEdgeGatewayRouteAuthService routeAuthService,
    CancellationToken cancellationToken) =>
{
    return !string.IsNullOrWhiteSpace(target) &&
           await routeAuthService.IsSafeReturnTargetAsync(target, cancellationToken)
        ? Results.Redirect(target.Trim())
        : Results.Redirect("/");
}).DisableAntiforgery();

app.MapGet("/api/passkeys/users/{userId:guid}", async (
    Guid userId,
    PasskeyAuthenticationService passkeyAuthenticationService,
    HttpContext context) =>
{
    var passkeys = await passkeyAuthenticationService.ListForUserAsync(userId, context.RequestAborted);
    return Results.Json(passkeys.Select(passkey => new
    {
        passkey.Id,
        passkey.FriendlyName,
        passkey.IsBackedUp,
        passkey.CreatedAtUtc,
        passkey.LastUsedAtUtc
    }));
});

app.MapDelete("/api/passkeys/{passkeyId:guid}", async (
    Guid passkeyId,
    PasskeyAuthenticationService passkeyAuthenticationService,
    HttpContext context) =>
{
    var result = await passkeyAuthenticationService.DeleteAsync(passkeyId, context.RequestAborted);
    return Results.Json(new { result.Succeeded, result.Message });
});

app.MapPost("/api/passkeys/users/{userId:guid}/enroll/options", async (
    Guid userId,
    HttpContext context,
    PasskeyAuthenticationService passkeyAuthenticationService,
    ILogger<Program> logger) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<PasskeyEnrollmentOptionsRequest>(
            cancellationToken: context.RequestAborted) ?? new PasskeyEnrollmentOptionsRequest(null);
        var result = await passkeyAuthenticationService.BuildRegistrationOptionsAsync(
            userId,
            request.FriendlyName ?? string.Empty,
            context.Request,
            context.RequestAborted);

        return BuildPasskeyOptionsResponse(result);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Passkey enrollment options request failed.");
        return Results.Json(
            new { succeeded = false, message = "Passkey setup could not start." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).DisableAntiforgery();

app.MapPost("/api/passkeys/me/enroll/options", async (
    HttpContext context,
    PasskeyAuthenticationService passkeyAuthenticationService,
    ILogger<Program> logger) =>
{
    try
    {
        var userIdValue = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            return Results.Json(
                new { succeeded = false, message = "Sign in with MFA before saving a passkey." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var request = await context.Request.ReadFromJsonAsync<PasskeyEnrollmentOptionsRequest>(
            cancellationToken: context.RequestAborted) ?? new PasskeyEnrollmentOptionsRequest(null);
        var result = await passkeyAuthenticationService.BuildRegistrationOptionsAsync(
            userId,
            request.FriendlyName ?? string.Empty,
            context.Request,
            context.RequestAborted);

        return BuildPasskeyOptionsResponse(result);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Current-user passkey enrollment options request failed.");
        return Results.Json(
            new { succeeded = false, message = "Passkey setup could not start." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization().DisableAntiforgery();

app.MapPost("/api/passkeys/register/complete", async (
    HttpContext context,
    PasskeyAuthenticationService passkeyAuthenticationService,
    ILogger<Program> logger) =>
{
    try
    {
        var (stateId, credentialJson, error) = await ReadPasskeyCeremonyRequestAsync(context);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Results.BadRequest(new { succeeded = false, message = error });
        }

        var result = await passkeyAuthenticationService.CompleteRegistrationAsync(
            stateId,
            credentialJson,
            context.Request,
            context.RequestAborted);
        return Results.Json(new { result.Succeeded, result.Message });
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Passkey registration completion request failed.");
        return Results.Json(
            new { succeeded = false, message = "Passkey setup failed." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).DisableAntiforgery();

app.MapPost("/api/passkeys/login/options", async (
    HttpContext context,
    PasskeyAuthenticationService passkeyAuthenticationService,
    ILogger<Program> logger) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<PasskeyLoginOptionsRequest>(
            cancellationToken: context.RequestAborted) ?? new PasskeyLoginOptionsRequest(null);
        var result = await passkeyAuthenticationService.BuildLoginOptionsAsync(
            request.Email ?? string.Empty,
            context.Request,
            context.RequestAborted);

        return BuildPasskeyOptionsResponse(result);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Passkey sign-in options request failed.");
        return Results.Json(
            new { succeeded = false, message = "Passkey sign-in could not start." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).DisableAntiforgery();

app.MapPost("/api/passkeys/login/complete", async (
    HttpContext context,
    PasskeyAuthenticationService passkeyAuthenticationService,
    ILogger<Program> logger) =>
{
    try
    {
        var (stateId, credentialJson, error) = await ReadPasskeyCeremonyRequestAsync(context);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Results.BadRequest(new { succeeded = false, message = error });
        }

        var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
        var result = await passkeyAuthenticationService.CompleteLoginAsync(
            stateId,
            credentialJson,
            context.Request,
            context.RequestAborted);
        if (!result.Succeeded || result.User is null)
        {
            return Results.Json(new { succeeded = false, message = result.ErrorMessage });
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User.Id.ToString("D")),
            new(ClaimTypes.Name, result.User.Email),
            new(ClaimTypes.Email, result.User.Email),
            new("amr", "passkey"),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(result.User.SessionLifetimeMinutes));
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = expiresUtc
            });

        return Results.Json(new { succeeded = true, redirectUrl = returnUrl });
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Passkey sign-in completion request failed.");
        return Results.Json(
            new { succeeded = false, message = "Passkey sign-in failed." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).DisableAntiforgery();

app.MapGet("/api/status", async (
    HttpContext httpContext,
    IEdgeGatewayStatusService statusService,
    CancellationToken cancellationToken) =>
{
    var ingressPath = httpContext.Request.Headers["X-Ingress-Path"].FirstOrDefault();
    return Results.Ok(await statusService.GetStatusAsync(ingressPath, cancellationToken));
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolvePath(string path) =>
    Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

static bool IsTrustedLocalPublicAssetRequest(HttpContext context)
{
    if (!string.IsNullOrWhiteSpace(context.Request.Headers["X-Forwarded-For"].ToString()))
    {
        return false;
    }

    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp is null)
    {
        return false;
    }

    return IPAddress.IsLoopback(remoteIp) ||
           remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4());
}

static bool IsJsonPublicAsset(string contentType, string path)
{
    var contentTypeOnly = (contentType ?? string.Empty).Split(';', 2)[0].Trim();
    return contentTypeOnly.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
           contentTypeOnly.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

static PublicAssetResponse MapPublicAsset(WellKnownService service) =>
    new(
        service.Id,
        service.Domain,
        service.RelativePath,
        service.ContentType,
        service.PublicUrl,
        service.Enabled,
        service.RequiresAuth,
        service.CacheControl,
        service.LastPublishedUtc,
        service.LastVerifiedUtc,
        service.LastVerificationStatus,
        service.LastVerificationMessage);

static string NormalizeForwardedHost(string? host)
{
    var value = (host ?? string.Empty).Split(',', 2)[0].Trim().TrimEnd('.');
    var portIndex = value.IndexOf(':', StringComparison.Ordinal);
    return portIndex > -1 ? value[..portIndex] : value;
}

static string NormalizeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        return "/";
    }

    var trimmed = returnUrl.Trim();
    return trimmed.StartsWith("/", StringComparison.Ordinal) &&
           !trimmed.StartsWith("//", StringComparison.Ordinal) &&
           !trimmed.StartsWith("/\\", StringComparison.Ordinal)
        ? trimmed
        : "/";
}

static string BuildLoginRedirectTarget(
    string returnUrl,
    string? errorMessage,
    string? email,
    string? notice = null,
    string? method = null,
    bool passkeySetup = false)
{
    var query = new List<string>
    {
        $"returnUrl={Uri.EscapeDataString(NormalizeReturnUrl(returnUrl))}"
    };

    if (!string.IsNullOrWhiteSpace(errorMessage))
    {
        query.Add($"error={Uri.EscapeDataString(errorMessage.Trim())}");
    }

    if (!string.IsNullOrWhiteSpace(email))
    {
        query.Add($"email={Uri.EscapeDataString(email.Trim())}");
    }

    if (!string.IsNullOrWhiteSpace(notice))
    {
        query.Add($"notice={Uri.EscapeDataString(notice.Trim())}");
    }

    if (!string.IsNullOrWhiteSpace(method))
    {
        query.Add($"method={Uri.EscapeDataString(NormalizeAuthMethod(method))}");
    }

    if (passkeySetup)
    {
        query.Add("passkeySetup=1");
    }

    return $"/login?{string.Join('&', query)}";
}

static string NormalizeAuthMethod(string? method)
{
    var normalized = (method ?? string.Empty).Trim().ToLowerInvariant();
    return normalized is "passkey" or "email" or "authenticator"
        ? normalized
        : "authenticator";
}

static bool IsTruthy(string? value) =>
    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

static IResult BuildPasskeyOptionsResponse(PasskeyOptionsResult result)
{
    if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StateId) || string.IsNullOrWhiteSpace(result.OptionsJson))
    {
        return Results.Json(new { succeeded = false, message = result.ErrorMessage });
    }

    return Results.Content(
        $$"""{"succeeded":true,"stateId":"{{result.StateId}}","options":{{result.OptionsJson}}}""",
        "application/json",
        Encoding.UTF8);
}

static async Task<(string StateId, string CredentialJson, string? Error)> ReadPasskeyCeremonyRequestAsync(
    HttpContext context)
{
    try
    {
        using var document = await JsonDocument.ParseAsync(
            context.Request.Body,
            cancellationToken: context.RequestAborted);
        var root = document.RootElement;
        if (!root.TryGetProperty("stateId", out var stateIdElement) ||
            string.IsNullOrWhiteSpace(stateIdElement.GetString()))
        {
            return (string.Empty, string.Empty, "The passkey state was missing.");
        }

        if (!root.TryGetProperty("credential", out var credentialElement))
        {
            return (string.Empty, string.Empty, "The passkey credential response was missing.");
        }

        return (stateIdElement.GetString()!, credentialElement.GetRawText(), null);
    }
    catch (JsonException)
    {
        return (string.Empty, string.Empty, "The passkey request was not valid JSON.");
    }
}

sealed record PasskeyEnrollmentOptionsRequest(string? FriendlyName);

sealed record PasskeyLoginOptionsRequest(string? Email);

sealed record PublicAssetPublishRequest(
    string Hostname,
    string Path,
    string ContentType,
    string Content,
    string Description,
    string CacheControl = "no-store");

sealed record PublicAssetResponse(
    Guid Id,
    string Hostname,
    string Path,
    string ContentType,
    string PublicUrl,
    bool Enabled,
    bool RequiresAuth,
    string CacheControl,
    DateTimeOffset? LastPublishedUtc,
    DateTimeOffset? LastVerifiedUtc,
    string LastVerificationStatus,
    string LastVerificationMessage);
