namespace LMS.EdgeGateway.Core;

public interface ICloudflareClient
{
    Task<TResult> GetAsync<TResult>(
        string apiToken,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TResult>> GetAllPagesAsync<TResult>(
        string apiToken,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default);

    Task<TResult> PostAsync<TRequest, TResult>(
        string apiToken,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default);

    Task<TResult> PutAsync<TRequest, TResult>(
        string apiToken,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default);

    Task<TResult> PatchAsync<TRequest, TResult>(
        string apiToken,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string apiToken,
        string path,
        CancellationToken cancellationToken = default);
}
