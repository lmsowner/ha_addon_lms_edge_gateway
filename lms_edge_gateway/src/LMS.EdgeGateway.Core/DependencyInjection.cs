using Microsoft.Extensions.DependencyInjection;

namespace LMS.EdgeGateway.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddEdgeGatewayCore(this IServiceCollection services)
    {
        services.AddSingleton<IProcessStatusProbe, ProcessStatusProbe>();
        services.AddSingleton<IEdgeGatewayConfigurationStore, JsonEdgeGatewayConfigurationStore>();
        services.AddSingleton<ICloudflareApiTokenStore, JsonCloudflareApiTokenStore>();
        services.AddHttpClient<ICloudflareApiTokenValidator, CloudflareApiTokenValidator>(client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        });
        services.AddHttpClient<ICloudflareZoneService, CloudflareZoneService>(client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        });
        services.AddScoped<IEdgeGatewayStatusService, EdgeGatewayStatusService>();
        return services;
    }
}
