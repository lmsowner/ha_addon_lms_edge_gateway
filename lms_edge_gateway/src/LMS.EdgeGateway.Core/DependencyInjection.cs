using Microsoft.Extensions.DependencyInjection;

namespace LMS.EdgeGateway.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddEdgeGatewayCore(this IServiceCollection services)
    {
        services.AddSingleton<IProcessStatusProbe, ProcessStatusProbe>();
        services.AddSingleton<IEdgeGatewayConfigurationStore, JsonEdgeGatewayConfigurationStore>();
        services.AddSingleton<IEdgeGatewaySecurityStore, JsonEdgeGatewaySecurityStore>();
        services.AddSingleton<IEdgeGatewaySecretProtector, EdgeGatewaySecretProtector>();
        services.AddSingleton<ICloudflareApiTokenStore, JsonCloudflareApiTokenStore>();
        services.AddHttpClient<ICloudflareApiTokenValidator, CloudflareApiTokenValidator>(client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        });
        services.AddHttpClient<ICloudflareZoneService, CloudflareZoneService>(client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        });
        services.AddHttpClient<ICloudflareClient, CloudflareClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
        });
        services.AddScoped<ICloudflareDnsService, CloudflareDnsService>();
        services.AddScoped<ICloudflareTunnelService, CloudflareTunnelService>();
        services.AddScoped<IEdgeGatewayRelayProvisioningService, EdgeGatewayRelayProvisioningService>();
        services.AddScoped<IEdgeGatewayStatusService, EdgeGatewayStatusService>();
        services.AddScoped<IEdgeGatewayRouteAuthService, EdgeGatewayRouteAuthService>();
        services.AddScoped<ILocalHttpServiceDiscoveryService, LocalHttpServiceDiscoveryService>();
        services.AddScoped<IEmailApiProvider, ResendEmailProvider>();
        services.AddScoped<IEmailApiProvider, BrevoEmailProvider>();
        services.AddScoped<IEmailApiProvider, MailerSendEmailProvider>();
        services.AddScoped<IEmailApiProvider, MailgunEmailProvider>();
        services.AddScoped<EmailProviderFactory>();
        services.AddScoped<IEdgeGatewayEmailDeliveryService, EdgeGatewayEmailDeliveryService>();
        services.AddScoped<IEmailSender>(provider => provider.GetRequiredService<IEdgeGatewayEmailDeliveryService>());
        services.AddScoped<IEdgeGatewaySecurityService, EdgeGatewaySecurityService>();
        return services;
    }
}
