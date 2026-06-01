namespace LMS.EdgeGateway.Core;

public interface IWellKnownServiceStore
{
    Task<WellKnownConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        WellKnownConfiguration configuration,
        CancellationToken cancellationToken = default);
}
