using Microsoft.Extensions.DependencyInjection;

namespace LMS.EdgeGateway.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddEdgeGatewayCore(this IServiceCollection services)
    {
        services.AddSingleton<IEdgeGatewayAccessCheckPageStore, MemoryEdgeGatewayAccessCheckPageStore>();
        services.AddSingleton<IProcessStatusProbe, ProcessStatusProbe>();
        services.AddSingleton<IEdgeGatewayConfigurationStore, JsonEdgeGatewayConfigurationStore>();
        services.AddSingleton<IEdgeGatewaySecurityStore, JsonEdgeGatewaySecurityStore>();
        services.AddSingleton<IEdgeGatewayTemporaryIpApprovalStore, JsonEdgeGatewayTemporaryIpApprovalStore>();
        services.AddSingleton<IEdgeGatewaySecretProtector, EdgeGatewaySecretProtector>();
        services.AddSingleton<ICloudflareApiTokenStore, JsonCloudflareApiTokenStore>();
        services.AddSingleton<IWellKnownServiceStore, JsonWellKnownServiceStore>();
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
        services.AddScoped<IEdgeGatewayTemporaryIpApprovalService, EdgeGatewayTemporaryIpApprovalService>();
        services.AddSingleton<IDnsNameResolver, SystemDnsNameResolver>();
        services.AddSingleton<ILanLatencyProbe, PingLanLatencyProbe>();
        services.AddScoped<ILanClientTrustService, LanClientTrustService>();
        services.AddScoped<ILocalHttpServiceDiscoveryService, LocalHttpServiceDiscoveryService>();
        services.AddScoped<IEmailApiProvider, ResendEmailProvider>();
        services.AddScoped<IEmailApiProvider, BrevoEmailProvider>();
        services.AddScoped<IEmailApiProvider, MailerSendEmailProvider>();
        services.AddScoped<IEmailApiProvider, MailgunEmailProvider>();
        services.AddScoped<EmailProviderFactory>();
        services.AddScoped<IEdgeGatewayEmailDeliveryService, EdgeGatewayEmailDeliveryService>();
        services.AddScoped<IEmailSender>(provider => provider.GetRequiredService<IEdgeGatewayEmailDeliveryService>());
        services.AddScoped<IEdgeGatewaySecurityService, EdgeGatewaySecurityService>();
        services.AddHttpClient<IWellKnownServiceManager, WellKnownServiceManager>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        return services;
    }
}
