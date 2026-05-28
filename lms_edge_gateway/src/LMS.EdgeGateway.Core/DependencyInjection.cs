using Microsoft.Extensions.DependencyInjection;

namespace LMS.EdgeGateway.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddEdgeGatewayCore(this IServiceCollection services)
    {
        services.AddSingleton<IProcessStatusProbe, ProcessStatusProbe>();
        services.AddSingleton<IEdgeGatewayConfigurationStore, JsonEdgeGatewayConfigurationStore>();
        services.AddScoped<IEdgeGatewayStatusService, EdgeGatewayStatusService>();
        return services;
    }
}
