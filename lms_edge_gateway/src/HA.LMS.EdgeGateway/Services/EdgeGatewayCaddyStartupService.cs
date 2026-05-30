using LMS.EdgeGateway.Core;

namespace HA.LMS.EdgeGateway.Services;

public sealed class EdgeGatewayCaddyStartupService(
    IServiceProvider serviceProvider,
    ILogger<EdgeGatewayCaddyStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var provisioningService = scope.ServiceProvider.GetRequiredService<IEdgeGatewayRelayProvisioningService>();
        var result = await provisioningService.RefreshCaddyConfigurationAsync(cancellationToken);
        if (result.Success)
        {
            logger.LogInformation("Caddy configuration refreshed on startup: {Summary}", result.Summary);
            return;
        }

        logger.LogWarning("Caddy configuration refresh failed on startup: {Summary}", result.Summary);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
