using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LMS.EdgeGateway.Core;

public sealed class MailRelayPublicIpMonitorService(
    IServiceScopeFactory scopeFactory,
    ILogger<MailRelayPublicIpMonitorService> logger) : BackgroundService, IMailRelayPublicIpMonitorService
{
    internal const int MinimumIntervalMinutes = 15;
    internal const int MaximumIntervalMinutes = 1_440;
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim checkLock = new(1, 1);

    public async Task<MailRelayConfiguration> SaveSettingsAsync(
        MailRelayPublicIpMonitorSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.CheckIntervalMinutes is < MinimumIntervalMinutes or > MaximumIntervalMinutes)
        {
            throw new InvalidOperationException(
                $"Choose a check interval between {MinimumIntervalMinutes} minutes and {MaximumIntervalMinutes / 60} hours.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMailRelayStore>();
        var configuration = await store.GetConfigurationAsync(cancellationToken)
            ?? throw new InvalidOperationException("Set up Mail Relay before configuring public IP monitoring.");
        if (!configuration.Enabled)
        {
            throw new InvalidOperationException("Mail Relay must be running before public IP monitoring can be enabled.");
        }

        configuration = configuration with
        {
            MonitorPublicIpChanges = request.Enabled,
            PublicIpCheckIntervalMinutes = request.CheckIntervalMinutes,
            PublicIpMonitorStatus = request.Enabled
                ? configuration.PublicIpMonitorStatus == MailRelayPublicIpMonitorStatus.Disabled
                    ? MailRelayPublicIpMonitorStatus.NotChecked
                    : configuration.PublicIpMonitorStatus
                : MailRelayPublicIpMonitorStatus.Disabled,
            PublicIpMonitorDetail = request.Enabled
                ? configuration.PublicIpMonitorStatus == MailRelayPublicIpMonitorStatus.Disabled
                    ? "Waiting for the first public IP and DNS check."
                    : configuration.PublicIpMonitorDetail
                : "Automatic public IP checks are disabled.",
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await store.SaveConfigurationAsync(configuration, cancellationToken);
        return configuration;
    }

    public Task<MailRelayPublicIpSyncResult> CheckNowAsync(CancellationToken cancellationToken = default) =>
        CheckCoreAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            await CheckIfDueAsync(stoppingToken);
            using var timer = new PeriodicTimer(SchedulerInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMailRelayStore>();
            var configuration = await store.GetConfigurationAsync(cancellationToken);
            if (configuration is null || !configuration.Enabled || !configuration.MonitorPublicIpChanges)
            {
                return;
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(
                configuration.PublicIpCheckIntervalMinutes,
                MinimumIntervalMinutes,
                MaximumIntervalMinutes));
            if (configuration.LastPublicIpCheckUtc is { } lastCheck &&
                DateTimeOffset.UtcNow - lastCheck < interval)
            {
                return;
            }

            await CheckCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The scheduled Mail Relay public IP check failed.");
        }
    }

    private async Task<MailRelayPublicIpSyncResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        await checkLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMailRelayStore>();
            var configuration = await store.GetConfigurationAsync(cancellationToken)
                ?? throw new InvalidOperationException("Set up Mail Relay before checking its public IP.");
            if (!configuration.Enabled)
            {
                throw new InvalidOperationException("Mail Relay is not running.");
            }

            var detector = scope.ServiceProvider.GetRequiredService<IMailRelayPreflightService>();
            var detected = await detector.DetectPublicIpv4Async(cancellationToken);
            if (!detected.Success)
            {
                var checkedAt = DateTimeOffset.UtcNow;
                var failed = configuration with
                {
                    LastPublicIpCheckUtc = checkedAt,
                    PublicIpMonitorStatus = MailRelayPublicIpMonitorStatus.Error,
                    PublicIpMonitorDetail = detected.Detail,
                    UpdatedUtc = checkedAt
                };
                await store.SaveConfigurationAsync(failed, cancellationToken);
                return new MailRelayPublicIpSyncResult(
                    false,
                    false,
                    configuration.PublicIpAddress,
                    string.Empty,
                    MailRelayPublicIpMonitorStatus.Error,
                    [],
                    detected.Detail,
                    checkedAt);
            }

            try
            {
                var provisioner = scope.ServiceProvider.GetRequiredService<IMailRelayProvisioningService>();
                var result = await provisioner.SynchronizePublicIpAsync(detected.Address, cancellationToken);
                logger.LogInformation(
                    "Mail Relay public IP check completed with status {Status}. Address changed: {AddressChanged}.",
                    result.Status,
                    result.PublicIpChanged);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var checkedAt = DateTimeOffset.UtcNow;
                var detail = $"Public IP and DNS synchronisation failed: {exception.Message}";
                await store.SaveConfigurationAsync(configuration with
                {
                    LastPublicIpCheckUtc = checkedAt,
                    PublicIpMonitorStatus = MailRelayPublicIpMonitorStatus.Error,
                    PublicIpMonitorDetail = detail,
                    UpdatedUtc = checkedAt
                }, cancellationToken);
                logger.LogError(exception, "Mail Relay public IP and DNS synchronisation failed.");
                return new MailRelayPublicIpSyncResult(
                    false,
                    false,
                    configuration.PublicIpAddress,
                    detected.Address,
                    MailRelayPublicIpMonitorStatus.Error,
                    [],
                    detail,
                    checkedAt);
            }
        }
        finally
        {
            checkLock.Release();
        }
    }
}
