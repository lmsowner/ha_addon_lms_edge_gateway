using Microsoft.AspNetCore.Http;

namespace HA.LMS.EdgeGateway.Services;

public sealed class AuthenticationCacheControlMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/lmshaauth", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/edge-auth", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/passkeys", StringComparison.OrdinalIgnoreCase))
        {
            // Apply last so endpoint and framework headers cannot make an auth response cacheable.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
