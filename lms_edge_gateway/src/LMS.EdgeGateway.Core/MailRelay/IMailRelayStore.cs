namespace LMS.EdgeGateway.Core;

public interface IMailRelayStore
{
    Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(MailRelayConfiguration configuration, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default);
    Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default);
    Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default);
    Task SaveClientAsync(MailRelayClient client, CancellationToken cancellationToken = default);
    Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default);
    Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default);
    Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
