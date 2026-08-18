namespace LMS.EdgeGateway.Core;

public sealed class EmailProviderFactory(IEnumerable<IEmailApiProvider> providers)
{
    private readonly IReadOnlyDictionary<MessagingEmailProvider, IEmailApiProvider> providerMap =
        providers.ToDictionary(provider => provider.Provider);

    public IEmailApiProvider Resolve(MessagingEmailProvider provider) =>
        providerMap.TryGetValue(provider, out var implementation)
            ? implementation
            : throw new InvalidOperationException($"Email provider {provider} is not implemented.");
}
