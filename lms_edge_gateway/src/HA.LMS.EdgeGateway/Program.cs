using HA.LMS.EdgeGateway.Components;
using LMS.EdgeGateway.Core;
using Microsoft.AspNetCore.DataProtection;

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
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", product = "LMS Edge Gateway Add-on" }));
app.MapPost("/api/setup/cloudflare-api-token", async (
    HttpContext httpContext,
    ICloudflareApiTokenStore tokenStore,
    ICloudflareApiTokenValidator tokenValidator,
    CancellationToken cancellationToken) =>
{
    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    var token = form["cloudflare_api_token"].ToString();

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
