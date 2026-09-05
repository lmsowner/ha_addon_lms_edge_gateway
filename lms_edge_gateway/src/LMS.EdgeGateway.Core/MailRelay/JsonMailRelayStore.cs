using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class JsonMailRelayStore(IOptions<EdgeGatewayCoreOptions> options) : IMailRelayStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(state => state.Configuration, cancellationToken);

    public Task SaveConfigurationAsync(MailRelayConfiguration configuration, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with { Configuration = configuration }, cancellationToken);

    public Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(state => state.Domains, cancellationToken);

    public Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with
        {
            Domains = state.Domains.Where(item => item.Id != domain.Id).Append(domain).ToArray()
        }, cancellationToken);

    public Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with
        {
            Domains = state.Domains.Where(item => item.Id != domainId).ToArray(),
            DnsRecords = state.DnsRecords.Where(item => item.MailRelayDomainId != domainId).ToArray()
        }, cancellationToken);

    public Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(state => state.Clients, cancellationToken);

    public Task SaveClientAsync(MailRelayClient client, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with
        {
            Clients = state.Clients.Where(item => item.Id != client.Id).Append(client).ToArray()
        }, cancellationToken);

    public Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with
        {
            Clients = state.Clients.Where(item => item.Id != clientId).ToArray()
        }, cancellationToken);

    public Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default) =>
        ReadAsync(state => (IReadOnlyList<MailRelayDnsRecord>)state.DnsRecords.Where(item => item.MailRelayDomainId == domainId).ToArray(), cancellationToken);

    public Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with
        {
            DnsRecords = state.DnsRecords.Where(item => item.Id != record.Id).Append(record).ToArray()
        }, cancellationToken);

    public Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default) =>
        WriteAsync(state => state with
        {
            DnsRecords = state.DnsRecords.Where(item => item.Id != recordId).ToArray()
        }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(_ => MailRelayStateDocument.Empty, cancellationToken);

    private async Task<T> ReadAsync<T>(Func<MailRelayStateDocument, T> read, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return read(await LoadUnlockedAsync(cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteAsync(Func<MailRelayStateDocument, MailRelayStateDocument> update, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadUnlockedAsync(cancellationToken);
            await SaveUnlockedAsync(update(state), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MailRelayStateDocument> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = GetStatePath();
        if (!File.Exists(path))
        {
            return MailRelayStateDocument.Empty;
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<MailRelayStateDocument>(stream, jsonOptions, cancellationToken);
        return Normalize(state);
    }

    private async Task SaveUnlockedAsync(MailRelayStateDocument state, CancellationToken cancellationToken)
    {
        var path = GetStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? options.Value.DataRoot);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, Normalize(state), jsonOptions, cancellationToken);
    }

    private string GetStatePath()
    {
        var dataRoot = options.Value.DataRoot;
        var root = Path.IsPathRooted(dataRoot)
            ? dataRoot
            : Path.GetFullPath(dataRoot);
        return Path.Combine(root, "mail-relay", "mail-relay.json");
    }

    private static MailRelayStateDocument Normalize(MailRelayStateDocument? state)
    {
        if (state is null)
        {
            return MailRelayStateDocument.Empty;
        }

        return state with
        {
            Domains = state.Domains ?? [],
            Clients = state.Clients ?? [],
            DnsRecords = state.DnsRecords ?? []
        };
    }
}
