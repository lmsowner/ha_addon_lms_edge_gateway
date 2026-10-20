using System.Net;
using HA.LMS.EdgeGateway.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class AuthenticationCacheControlTests
{
    [Fact]
    public async Task Login_and_auth_handoffs_are_never_cached_while_static_assets_keep_their_cache_policy()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseMiddleware<AuthenticationCacheControlMiddleware>();
        app.Run(context =>
        {
            // Exercise late endpoint headers as well as redirects carrying the new login cookie.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            if (context.Request.Path.StartsWithSegments("/lmshaauth") ||
                context.Request.Path.StartsWithSegments("/edge-auth"))
            {
                context.Response.Cookies.Append("test-session", "signed-in");
                context.Response.Redirect("/protected-app");
            }

            return context.Response.WriteAsync("response");
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Assert.Single(addresses.Addresses))
        };

        foreach (var path in new[]
                 {
                     "/login?returnUrl=%2Fprotected-app", "/LOGIN", "/login/setup",
                     "/lmshaauth/login", "/lmshaauth/logout", "/edge-auth/check",
                     "/edge-auth/return?target=https%3A%2F%2Fexample.com",
                     "/api/passkeys/login/options", "/api/passkeys/login/complete"
                 })
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.Headers.CacheControl!.NoStore, path);
            Assert.True(response.Headers.CacheControl.NoCache, path);
            Assert.Equal(TimeSpan.Zero, response.Headers.CacheControl.MaxAge);
            Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
            Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));
            if (path.StartsWith("/lmshaauth") || path.StartsWith("/edge-auth"))
            {
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal("/protected-app", response.Headers.Location!.OriginalString);
                Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("test-session="));
            }
        }

        foreach (var path in new[] { "/scripts/passkeys.fingerprint.js", "/app.fingerprint.css", "/login-help" })
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.Headers.CacheControl!.Public);
            Assert.False(response.Headers.CacheControl.NoStore);
        }

        await app.StopAsync();
    }
}
