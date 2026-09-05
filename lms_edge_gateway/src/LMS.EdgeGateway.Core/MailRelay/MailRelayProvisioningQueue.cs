using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LMS.EdgeGateway.Core;

public sealed class MailRelayProvisioningQueue(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<MailRelayProvisioningQueue> logger) : BackgroundService, IMailRelayProvisioningQueue
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, MailRelayProvisioningJobSnapshot> jobs = [];
    private readonly object enqueueLock = new();

    public Task<MailRelayProvisioningJobSnapshot> EnqueueAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (enqueueLock)
        {
            var active = jobs.Values
                .Where(item => !item.IsTerminal)
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            if (active is not null)
            {
                return Task.FromResult(active);
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = new MailRelayProvisioningJobSnapshot(
                Guid.NewGuid(),
                request,
                MailRelayProvisioningJobStatus.Queued,
                [],
                null,
                "Mail Relay setup is queued and will continue if this page reconnects.",
                now,
                null,
                null);
            jobs[snapshot.Id] = snapshot;
            if (!queue.Writer.TryWrite(snapshot.Id))
            {
                jobs.TryRemove(snapshot.Id, out _);
                throw new InvalidOperationException("Mail Relay setup could not be queued.");
            }

            PruneCompletedJobs(snapshot.Id);
            return Task.FromResult(snapshot);
        }
    }

    public MailRelayProvisioningJobSnapshot? GetJob(Guid jobId) =>
        jobs.TryGetValue(jobId, out var job) ? job : null;

    public MailRelayProvisioningJobSnapshot? GetLatestJob() =>
        jobs.Values.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!jobs.TryGetValue(jobId, out var queued))
            {
                continue;
            }

            Update(jobId, current => current with
            {
                Status = MailRelayProvisioningJobStatus.Running,
                Summary = "Mail Relay setup is running in the background. You can safely reload or reconnect.",
                StartedUtc = DateTimeOffset.UtcNow
            });

            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var provisioning = scope.ServiceProvider.GetRequiredService<IMailRelayProvisioningService>();
                var progress = new InlineProgress(update => UpdateProgress(jobId, update));
                var result = await provisioning.ProvisionAsync(queued.Request, progress, stoppingToken);
                Update(jobId, current => current with
                {
                    Status = result.Success
                        ? MailRelayProvisioningJobStatus.Succeeded
                        : MailRelayProvisioningJobStatus.Failed,
                    Steps = result.Steps.ToArray(),
                    Result = result,
                    Summary = result.Summary,
                    CompletedUtc = DateTimeOffset.UtcNow
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Mail Relay provisioning job {JobId} failed.", jobId);
                Update(jobId, current => current with
                {
                    Status = MailRelayProvisioningJobStatus.Failed,
                    Summary = exception.Message,
                    CompletedUtc = DateTimeOffset.UtcNow
                });
            }
        }
    }

    private void UpdateProgress(Guid jobId, MailRelaySetupProgressUpdate update) =>
        Update(jobId, current =>
        {
            var steps = current.Steps.ToList();
            var index = steps.FindIndex(item => item.Key == update.Key);
            if (index >= 0)
            {
                steps[index] = update;
            }
            else
            {
                steps.Add(update);
            }

            return current with { Steps = steps.ToArray(), Summary = update.Detail };
        });

    private void Update(
        Guid jobId,
        Func<MailRelayProvisioningJobSnapshot, MailRelayProvisioningJobSnapshot> update) =>
        jobs.AddOrUpdate(jobId, _ => throw new InvalidOperationException("Mail Relay job disappeared."), (_, current) => update(current));

    private void PruneCompletedJobs(Guid currentJobId)
    {
        foreach (var old in jobs.Values
                     .Where(item => item.Id != currentJobId && item.IsTerminal)
                     .OrderByDescending(item => item.CompletedUtc)
                     .Skip(4))
        {
            jobs.TryRemove(old.Id, out _);
        }
    }

    private sealed class InlineProgress(Action<MailRelaySetupProgressUpdate> report) : IProgress<MailRelaySetupProgressUpdate>
    {
        public void Report(MailRelaySetupProgressUpdate value) => report(value);
    }
}
