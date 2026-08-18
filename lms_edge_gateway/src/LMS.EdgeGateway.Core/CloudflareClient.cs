using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LMS.EdgeGateway.Core;

public sealed class CloudflareClient(HttpClient httpClient) : ICloudflareClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResult> GetAsync<TResult>(
        string apiToken,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<TResult>(HttpMethod.Get, apiToken, path, null, query, cancellationToken);
        return envelope.Result!;
    }

    public async Task<IReadOnlyList<TResult>> GetAllPagesAsync<TResult>(
        string apiToken,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TResult>();
        var page = 1;

        while (true)
        {
            var pageQuery = query is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(query, StringComparer.OrdinalIgnoreCase);

            pageQuery["page"] = page.ToString();
            pageQuery["per_page"] = "100";

            var envelope = await SendAsync<List<TResult>>(HttpMethod.Get, apiToken, path, null, pageQuery, cancellationToken);
            if (envelope.Result is { Count: > 0 } pageItems)
            {
                results.AddRange(pageItems);
            }

            if (envelope.ResultInfo is null ||
                envelope.ResultInfo.TotalPages <= page ||
                envelope.Result is null ||
                envelope.Result.Count == 0)
            {
                break;
            }

            page++;
        }

        return results;
    }

    public async Task<TResult> PostAsync<TRequest, TResult>(
        string apiToken,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<TResult>(HttpMethod.Post, apiToken, path, request, null, cancellationToken);
        return envelope.Result!;
    }

    public async Task<TResult> PutAsync<TRequest, TResult>(
        string apiToken,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<TResult>(HttpMethod.Put, apiToken, path, request, null, cancellationToken);
        return envelope.Result!;
    }

    public async Task<TResult> PatchAsync<TRequest, TResult>(
        string apiToken,
        string path,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<TResult>(HttpMethod.Patch, apiToken, path, request, null, cancellationToken);
        return envelope.Result!;
    }

    public async Task DeleteAsync(
        string apiToken,
        string path,
        CancellationToken cancellationToken = default)
    {
        await SendAsync<JsonElement>(HttpMethod.Delete, apiToken, path, null, null, cancellationToken);
    }

    private async Task<CloudflareEnvelope<TResult>> SendAsync<TResult>(
        HttpMethod method,
        string apiToken,
        string path,
        object? request,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(method, BuildUri(path, query));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (request is not null)
        {
            requestMessage.Content = JsonContent.Create(request, options: SerializerOptions);
        }

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        var envelope = await DeserializeEnvelopeAsync<TResult>(response, cancellationToken);

        if (response.IsSuccessStatusCode && envelope.Success)
        {
            return envelope;
        }

        throw new CloudflareApiException(
            (int)response.StatusCode,
            envelope.Errors.Select(MapError).ToArray());
    }

    private static async Task<CloudflareEnvelope<TResult>> DeserializeEnvelopeAsync<TResult>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<TResult>>(SerializerOptions, cancellationToken);
            if (envelope is not null)
            {
                return envelope;
            }
        }
        catch (JsonException exception)
        {
            throw new CloudflareApiException(
                (int)response.StatusCode,
                [new CloudflareApiError(0, "Cloudflare returned an unreadable response.", null, null)],
                exception);
        }

        return new CloudflareEnvelope<TResult>
        {
            Success = false,
            Errors =
            [
                new CloudflareMessageDto
                {
                    Code = 0,
                    Message = "Cloudflare returned an empty response."
                }
            ]
        };
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string?>? query)
    {
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxMadeSaneEdgeGateway/1.0");
        }

        var builder = new UriBuilder(new Uri(httpClient.BaseAddress!, path.TrimStart('/')));
        if (query is null || query.Count == 0)
        {
            return builder.Uri;
        }

        builder.Query = string.Join(
            "&",
            query
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));

        return builder.Uri;
    }

    private static CloudflareApiError MapError(CloudflareMessageDto error) =>
        new(error.Code, error.Message, error.DocumentationUrl, error.Source?.Pointer);
}
