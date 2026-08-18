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
        var result = await provisioningService.RefreshPublishedConfigurationAsync(cancellationToken);
        if (result.Success)
        {
            logger.LogInformation("Published configuration refreshed on startup: {Summary}", result.Summary);
            return;
        }

        logger.LogWarning("Published configuration refresh failed on startup: {Summary}", result.Summary);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
