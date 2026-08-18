namespace LMS.EdgeGateway.Core;

public interface IProcessStatusProbe
{
    Task<bool> IsRunningAsync(string processPattern, CancellationToken cancellationToken = default);
}
