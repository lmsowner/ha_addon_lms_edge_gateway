using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed partial class LocalHttpServiceDiscoveryService(IOptions<EdgeGatewayCoreOptions> options) : ILocalHttpServiceDiscoveryService
{
    private const int MaxConcurrentCacheValidation = 256;
    private static readonly int[] ApprovedPorts = [80, 443, 3000, 3001, 5000, 5001, 5080, 7000, 7126, 8000, 8080, 8081, 8123, 8443, 8888, 9000, 9090, 9443, 10000, 11434, 32400];
    private static readonly int[] ExpandedPorts = [81, 82, 88, 800, 808, 10443, 18080, 1880, 2283, 2342, 50000, 50001];
    private static readonly int[] HostLivenessPorts = [80, 443, 8080, 8123, 8443, 9443, 5000, 3000, 9000, 10000, 32400, 22, 53, 139, 445];
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ProbePaceDelay = TimeSpan.Zero;
    private readonly SemaphoreSlim cacheMutationLock = new(1, 1);

    [GeneratedRegex("<title[^>]*>\\s*(?<title>.*?)\\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> GetCachedAsync(CancellationToken cancellationToken = default) =>
        SortEndpoints(await ReadCacheAsync(cancellationToken));

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> ValidateCachedAsync(
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await cacheMutationLock.WaitAsync(cancellationToken);
        try
        {
            var cached = SortEndpoints(await ReadCacheAsync(cancellationToken));
            if (cached.Count == 0)
            {
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    "No cached HTTP/S service candidates to check.",
                    0,
                    0,
                    0,
                    IsCompleted: true));
                return [];
            }

            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Checking {cached.Count} cached HTTP/S service candidate(s).",
                0,
                cached.Count,
                0));

            var state = new DiscoveryProgressState(cached.Count);
            var live = new ConcurrentDictionary<string, LocalHttpServiceEndpoint>(StringComparer.OrdinalIgnoreCase);
            using var concurrency = new SemaphoreSlim(MaxConcurrentCacheValidation);
            await Task.WhenAll(cached.Select(endpoint => ValidateCachedEndpointWithLimitAsync(endpoint, concurrency, state, live, progress, cancellationToken)));

            var validated = SortEndpoints(live.Values);
            await WriteCacheAsync(validated, cancellationToken);
            var removedCount = Math.Max(0, cached.Count - validated.Count);
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                removedCount == 0
                    ? $"Cached HTTP/S services checked. {validated.Count} still live."
                    : $"Cached HTTP/S services checked. Removed {removedCount} stale item(s); {validated.Count} still live.",
                cached.Count,
                cached.Count,
                validated.Count,
                IsCompleted: true));
            return validated;
        }
        finally
        {
            cacheMutationLock.Release();
        }
    }

    public Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        DiscoverAsync(new LocalHttpServiceDiscoveryRequest(), cancellationToken);

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default) =>
        await DiscoverAsync(request, null, cancellationToken);

    public async Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(
        LocalHttpServiceDiscoveryRequest request,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        var settings = DiscoverySettings.Load(options.Value);
        var adapters = new IDiscoveryAdapter[]
        {
            new HomeAssistantDiscoveryAdapter(settings),
            new DockerDiscoveryAdapter(settings),
            new LanDiscoveryAdapter(settings)
        };

        var evidence = new List<DiscoveryEvidence>();
        foreach (var adapter in adapters)
        {
            if (!adapter.IsEnabled(request))
            {
                if (adapter is DockerDiscoveryAdapter && request.IncludeDocker)
                {
                    progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                        "Docker discovery skipped. Enable advanced_docker_discovery in add-on options before using Docker API evidence.",
                        0,
                        0,
                        evidence.Count));
                }

                continue;
            }

            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"{adapter.Name}: preparing...",
                0,
                0,
                evidence.Count));

            evidence.AddRange(await adapter.DiscoverAsync(request, progress, cancellationToken));
        }

        var correlated = DiscoveryCorrelator.Correlate(evidence);
        var requestedScopes = BuildRequestedScopes(request);
        IReadOnlyList<LocalHttpServiceEndpoint> sorted;
        await cacheMutationLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadCacheAsync(cancellationToken);
            var merged = existing
                .Where(endpoint => !requestedScopes.Contains(endpoint.Scope))
                .Concat(correlated)
                .DistinctBy(BuildEndpointKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await WriteCacheAsync(merged, cancellationToken);
            sorted = SortEndpoints(merged);
        }
        finally
        {
            cacheMutationLock.Release();
        }

        progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
            correlated.Count == 0
                ? "Discovery completed. No HTTP/S service candidates were found."
                : $"Discovery completed. {correlated.Count} service candidate(s) available.",
            correlated.Count,
            correlated.Count,
            correlated.Count,
            IsCompleted: true));
        return sorted;
    }

    private async Task<IReadOnlyList<LocalHttpServiceEndpoint>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        var path = GetCachePath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, LocalHttpServiceDiscoveryJsonContext.Default.LocalHttpServiceEndpointArray, cancellationToken) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task WriteCacheAsync(IReadOnlyList<LocalHttpServiceEndpoint> endpoints, CancellationToken cancellationToken)
    {
        var path = GetCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, endpoints.ToArray(), LocalHttpServiceDiscoveryJsonContext.Default.LocalHttpServiceEndpointArray, cancellationToken);
    }

    private string GetCachePath() => Path.Combine(ResolvePath(options.Value.DataRoot), "http-services-cache.json");

    private static IReadOnlyList<LocalHttpServiceEndpoint> SortEndpoints(IEnumerable<LocalHttpServiceEndpoint> endpoints) =>
        endpoints
            .DistinctBy(BuildEndpointKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(endpoint => endpoint.Exposure switch
            {
                DiscoveryExposure.Publishable => 0,
                DiscoveryExposure.RequiresManualConfirmation => 1,
                DiscoveryExposure.InternalOnly => 2,
                DiscoveryExposure.UnsafeToExpose => 3,
                _ => 4
            })
            .ThenByDescending(endpoint => endpoint.Confidence)
            .ThenBy(endpoint => endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Port)
            .ToArray();

    private static HashSet<string> BuildRequestedScopes(LocalHttpServiceDiscoveryRequest request)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.IncludeLocalhost) scopes.Add("Localhost");
        if (request.IncludeLan) scopes.Add("LAN");
        if (request.IncludeTailnet) scopes.Add("Tailnet");
        if (request.IncludeDocker) scopes.Add("Docker");
        scopes.Add("Home Assistant");
        return scopes;
    }

    private static async Task ValidateCachedEndpointWithLimitAsync(
        LocalHttpServiceEndpoint endpoint,
        SemaphoreSlim concurrency,
        DiscoveryProgressState state,
        ConcurrentDictionary<string, LocalHttpServiceEndpoint> live,
        IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            var refreshed = await TryRefreshCachedEndpointAsync(endpoint, cancellationToken);
            if (refreshed is not null)
            {
                live[BuildEndpointKey(refreshed)] = refreshed;
                var foundCount = state.IncrementFoundCount();
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    $"Cached service still live at {refreshed.Host}:{refreshed.Port}.",
                    state.ProbedCount,
                    state.TotalProbeCount,
                    foundCount,
                    refreshed));
            }
        }
        finally
        {
            var checkedCount = state.IncrementProbedCount();
            if (checkedCount == state.TotalProbeCount || checkedCount % 4 == 0)
            {
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    $"Checked {checkedCount}/{state.TotalProbeCount} cached HTTP/S service candidate(s).",
                    checkedCount,
                    state.TotalProbeCount,
                    state.FoundCount));
            }

            concurrency.Release();
        }
    }

    private static async Task<LocalHttpServiceEndpoint?> TryRefreshCachedEndpointAsync(
        LocalHttpServiceEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Port is <= 0 or > 65535)
        {
            return null;
        }

        var probeAddressName = FirstNonBlank(endpoint.IpAddress, endpoint.Host);
        if (string.IsNullOrWhiteSpace(probeAddressName))
        {
            return null;
        }

        var probeAddress = IPAddress.TryParse(probeAddressName, out var parsedAddress) ? parsedAddress : null;
        var targetHost = FirstNonBlank(endpoint.Host, probeAddressName) ?? probeAddressName;
        var probeHost = new ProbeHost(
            probeAddressName,
            probeAddress,
            targetHost,
            endpoint.Scope,
            FirstNonBlank(endpoint.IpAddress, probeAddress?.ToString()),
            endpoint.DisplayName,
            endpoint.Scope.Equals("Localhost", StringComparison.OrdinalIgnoreCase),
            true,
            [endpoint.Port]);

        foreach (var scheme in BuildCachedValidationSchemes(endpoint))
        {
            var evidence = await HttpFingerprintProbe.ProbeAsync(probeHost, endpoint.Port, scheme, cancellationToken);
            if (evidence is null)
            {
                continue;
            }

            var refreshed = DiscoveryCorrelator.ToEndpoint([evidence]);
            return string.IsNullOrWhiteSpace(refreshed.DisplayName) && !string.IsNullOrWhiteSpace(endpoint.DisplayName)
                ? refreshed with { DisplayName = endpoint.DisplayName }
                : refreshed;
        }

        return null;
    }

    private static IReadOnlyList<string> BuildCachedValidationSchemes(LocalHttpServiceEndpoint endpoint)
    {
        var schemes = new List<string>();
        AddScheme(endpoint.Scheme);
        AddScheme(GuessSchemeFromPort(endpoint.Port));
        AddScheme(Uri.UriSchemeHttp);
        AddScheme(Uri.UriSchemeHttps);
        return schemes;

        void AddScheme(string? scheme)
        {
            if ((scheme?.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) == true ||
                 scheme?.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) == true) &&
                !schemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
            {
                schemes.Add(scheme);
            }
        }
    }

    private static string BuildEndpointKey(LocalHttpServiceEndpoint endpoint) =>
        $"{endpoint.Scheme}|{FirstNonBlank(endpoint.IpAddress, endpoint.Host)}|{endpoint.Port}|{endpoint.ServiceKind}|{endpoint.Fingerprint}";

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

    private static HttpClient BuildSupervisorClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri("http://supervisor"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement?> ReadSupervisorJsonAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private interface IDiscoveryAdapter
    {
        string Name { get; }
        bool IsEnabled(LocalHttpServiceDiscoveryRequest request);
        Task<IReadOnlyList<DiscoveryEvidence>> DiscoverAsync(
            LocalHttpServiceDiscoveryRequest request,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken);
    }

    private sealed class LanDiscoveryAdapter(DiscoverySettings settings) : IDiscoveryAdapter
    {
        private const int MaxConcurrentHostChecks = 512;
        private const int MaxConcurrentTcpLivenessChecks = 2048;
        private const int MaxConcurrentProbes = 512;
        public string Name => "LAN discovery";

        public bool IsEnabled(LocalHttpServiceDiscoveryRequest request) => request.IncludeLocalhost || request.IncludeLan || request.IncludeTailnet;

        public async Task<IReadOnlyList<DiscoveryEvidence>> DiscoverAsync(
            LocalHttpServiceDiscoveryRequest request,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            var hosts = new List<ProbeHost>();
            if (request.IncludeLocalhost)
            {
                hosts.AddRange(BuildLocalhostHosts());
            }

            if (request.IncludeLan)
            {
                hosts.AddRange(await BuildLanHostsAsync(settings, cancellationToken));
            }

            if (request.IncludeTailnet)
            {
                hosts.AddRange(await BuildTailnetHostsAsync(cancellationToken));
            }

            var distinctHosts = hosts
                .DistinctBy(host => $"{host.Scope}|{host.ProbeAddressName}", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var portCount = BuildPortList(settings).Count;
            var progressState = new HostDiscoveryProgressState(distinctHosts.Length);
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                distinctHosts.Length == 0 ? "No local subnet scan targets." : $"Checking {distinctHosts.Length} local address(es); live hosts are scanned across {portCount} approved HTTP/S port(s) immediately.",
                0,
                distinctHosts.Length,
                0));

            var results = new ConcurrentBag<DiscoveryEvidence>();
            using var hostConcurrency = new SemaphoreSlim(MaxConcurrentHostChecks);
            using var tcpConcurrency = new SemaphoreSlim(MaxConcurrentTcpLivenessChecks);
            using var serviceConcurrency = new SemaphoreSlim(MaxConcurrentProbes);
            await Task.WhenAll(distinctHosts.Select(host => ProbeHostWhenReachableAsync(
                host,
                settings,
                hostConcurrency,
                tcpConcurrency,
                serviceConcurrency,
                results,
                progressState,
                progress,
                cancellationToken)));
            return results.ToArray();
        }

        private static async Task ProbeHostWhenReachableAsync(
            ProbeHost host,
            DiscoverySettings settings,
            SemaphoreSlim hostConcurrency,
            SemaphoreSlim tcpConcurrency,
            SemaphoreSlim serviceConcurrency,
            ConcurrentBag<DiscoveryEvidence> results,
            HostDiscoveryProgressState progressState,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            bool isReachable;
            await hostConcurrency.WaitAsync(cancellationToken);
            try
            {
                isReachable = host.IsLocalProbe ||
                              host.IsKnownLive ||
                              host.ProbeAddress is not null &&
                              (await CanPingAsync(host.ProbeAddress, cancellationToken) ||
                               await CanOpenAnyTcpAsync(host.ProbeAddress, HostLivenessPorts, tcpConcurrency, cancellationToken));
            }
            finally
            {
                var checkedCount = progressState.IncrementCheckedCount();
                if (checkedCount == progressState.TotalHostCount || checkedCount % 16 == 0)
                {
                    progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                        $"Checked {checkedCount}/{progressState.TotalHostCount} local address(es), {progressState.LiveCount} live, {progressState.FoundCount} service(s).",
                        checkedCount,
                        progressState.TotalHostCount,
                        progressState.FoundCount));
                }

                hostConcurrency.Release();
            }

            if (!isReachable)
            {
                return;
            }

            var liveCount = progressState.IncrementLiveCount();
            var ports = host.KnownPorts.Count > 0 ? host.KnownPorts : BuildPortList(settings);
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Live host {host.ProbeAddressName}; scanning {ports.Count} HTTP/S port(s).",
                progressState.CheckedCount,
                progressState.TotalHostCount,
                progressState.FoundCount));

            await Task.WhenAll(ports.Select(port => ProbeServicePortAsync(
                host,
                port,
                serviceConcurrency,
                results,
                progressState,
                progress,
                cancellationToken)));
        }

        private static async Task ProbeServicePortAsync(
            ProbeHost host,
            int port,
            SemaphoreSlim serviceConcurrency,
            ConcurrentBag<DiscoveryEvidence> results,
            HostDiscoveryProgressState progressState,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            await serviceConcurrency.WaitAsync(cancellationToken);
            try
            {
                await Task.Delay(ProbePaceDelay, cancellationToken);
                var probeTasks = GuessSchemes(port)
                    .Select(scheme => HttpFingerprintProbe.ProbeAsync(host, port, scheme, cancellationToken))
                    .ToArray();
                var probeResults = await Task.WhenAll(probeTasks);

                foreach (var evidence in probeResults.Where(evidence => evidence is not null).Cast<DiscoveryEvidence>())
                {
                    results.Add(evidence);
                    var foundCount = progressState.IncrementFoundCount();
                    progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                        $"HTTP/S response at {evidence.Host}:{evidence.Port}.",
                        progressState.CheckedCount,
                        progressState.TotalHostCount,
                        foundCount,
                        DiscoveryCorrelator.ToEndpoint([evidence])));
                }
            }
            finally
            {
                serviceConcurrency.Release();
            }
        }

        private static async Task<IReadOnlyList<ProbeHost>> FindReachableHostsAsync(
            IReadOnlyList<ProbeHost> hosts,
            DiscoverySettings settings,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (hosts.Count == 0)
            {
                return [];
            }

            var reachable = new ConcurrentBag<ProbeHost>();
            var progressState = new HostDiscoveryProgressState(hosts.Count);
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Checking {hosts.Count} local address(es) for live hosts by ping or TCP connect.",
                0,
                hosts.Count,
                0));

            using var concurrency = new SemaphoreSlim(MaxConcurrentHostChecks);
            using var tcpConcurrency = new SemaphoreSlim(MaxConcurrentTcpLivenessChecks);
            await Task.WhenAll(hosts.Select(host => CheckHostReachabilityAsync(host, concurrency, tcpConcurrency, reachable, progressState, progress, cancellationToken)));
            return reachable
                .DistinctBy(host => $"{host.Scope}|{host.ProbeAddressName}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(host => host.Scope.Equals("Localhost", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(host => host.ProbeAddress is null ? uint.MaxValue : AddressToUInt32(host.ProbeAddress))
                .ToArray();
        }

        private static async Task CheckHostReachabilityAsync(
            ProbeHost host,
            SemaphoreSlim concurrency,
            SemaphoreSlim tcpConcurrency,
            ConcurrentBag<ProbeHost> reachable,
            HostDiscoveryProgressState progressState,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                if (host.IsLocalProbe ||
                    host.IsKnownLive ||
                    host.ProbeAddress is not null &&
                    (await CanPingAsync(host.ProbeAddress, cancellationToken) ||
                     await CanOpenAnyTcpAsync(host.ProbeAddress, HostLivenessPorts, tcpConcurrency, cancellationToken)))
                {
                    reachable.Add(host);
                    progressState.IncrementLiveCount();
                }
            }
            finally
            {
                var checkedCount = progressState.IncrementCheckedCount();
                if (checkedCount == progressState.TotalHostCount || checkedCount % 16 == 0)
                {
                    progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                        $"Checked {checkedCount}/{progressState.TotalHostCount} local address(es), {progressState.LiveCount} live.",
                        checkedCount,
                        progressState.TotalHostCount,
                        progressState.LiveCount));
                }

                concurrency.Release();
            }
        }

        private static IReadOnlyList<ProbeHost> BuildLocalhostHosts()
        {
            var ports = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .Distinct()
                .Intersect(ApprovedPorts.Concat(ExpandedPorts))
                .ToArray();

            return ports.Length == 0
                ? []
                : [new ProbeHost("localhost", IPAddress.Loopback, "localhost", "Localhost", "127.0.0.1", "Local host", true, true, ports)];
        }

        private static async Task<IReadOnlyList<ProbeHost>> BuildLanHostsAsync(DiscoverySettings settings, CancellationToken cancellationToken)
        {
            var ports = BuildPortList(settings);
            var knownAddresses = await LoadLanNeighbourAddressesAsync(cancellationToken);
            var supervisorCidrs = await LoadSupervisorLanCidrsAsync(settings, cancellationToken);
            var configuredCidrs = settings.Cidrs
                .Concat(supervisorCidrs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cidrAddresses = configuredCidrs.Length > 0 ? ExpandCidrs(configuredCidrs) : [];
            var fallbackAddresses = cidrAddresses.Count == 0 ? ExpandLocalInterfaceCidrs() : [];
            var neighbourAddresses = knownAddresses
                .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
                .Where(address => address is not null)
                .Cast<IPAddress>();
            var addresses = cidrAddresses
                .Concat(fallbackAddresses)
                .Concat(neighbourAddresses);

            return addresses
                .Distinct(IPAddressComparer.Instance)
                .Select(address =>
                {
                    var ip = address.ToString();
                    var isKnownLive = knownAddresses.Contains(ip);
                    return new ProbeHost(
                        ip,
                        address,
                        ip,
                        "LAN",
                        ip,
                        isKnownLive ? "Known LAN neighbour" : "LAN candidate",
                        false,
                        isKnownLive,
                        ports);
                })
                .ToArray();
        }

        private static async Task<IReadOnlyList<string>> LoadSupervisorLanCidrsAsync(DiscoverySettings settings, CancellationToken cancellationToken)
        {
            if (!settings.HasSupervisorToken)
            {
                return [];
            }

            using var client = BuildSupervisorClient(settings.SupervisorToken);
            var networkInfo = await ReadSupervisorJsonAsync(client, "/network/info", cancellationToken);
            return ExtractSupervisorLanCidrs(networkInfo);
        }

        private static IReadOnlyList<string> ExtractSupervisorLanCidrs(JsonElement? payload)
        {
            if (payload is null ||
                !payload.Value.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("interfaces", out var interfaces))
            {
                return [];
            }

            var cidrs = interfaces.ValueKind switch
            {
                JsonValueKind.Array => interfaces.EnumerateArray()
                    .SelectMany(item => ExtractSupervisorInterfaceCidrs(item, GetJsonString(item, "interface"))),
                JsonValueKind.Object => interfaces.EnumerateObject()
                    .SelectMany(property => ExtractSupervisorInterfaceCidrs(property.Value, property.Name)),
                _ => []
            };

            return cidrs
                .Where(IsPrivateCidr)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IEnumerable<string> ExtractSupervisorInterfaceCidrs(JsonElement item, string? fallbackName)
        {
            var name = FirstNonBlank(GetJsonString(item, "interface"), fallbackName) ?? string.Empty;
            if (IsContainerInterfaceName(name) ||
                IsExplicitlyFalse(item, "enabled") ||
                IsExplicitlyFalse(item, "connected"))
            {
                yield break;
            }

            if (item.TryGetProperty("ipv4", out var ipv4))
            {
                foreach (var cidr in ExtractIpv4Cidrs(ipv4))
                {
                    yield return cidr;
                }
            }

            foreach (var cidr in ExtractIpv4Cidrs(item))
            {
                yield return cidr;
            }
        }

        private static IEnumerable<string> ExtractIpv4Cidrs(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var cidr in ExtractIpv4Cidrs(item))
                    {
                        yield return cidr;
                    }
                }

                yield break;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var name in new[] { "ip_address", "address", "addresses" })
            {
                if (!element.TryGetProperty(name, out var property))
                {
                    continue;
                }

                if (property.ValueKind == JsonValueKind.String)
                {
                    var cidr = BuildIpv4Cidr(property.GetString(), element);
                    if (!string.IsNullOrWhiteSpace(cidr))
                    {
                        yield return cidr;
                    }
                }
                else if (property.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var cidr = BuildIpv4Cidr(item.GetString(), element);
                            if (!string.IsNullOrWhiteSpace(cidr))
                            {
                                yield return cidr;
                            }
                        }
                        else
                        {
                            foreach (var cidr in ExtractIpv4Cidrs(item))
                            {
                                yield return cidr;
                            }
                        }
                    }
                }
                else if (property.ValueKind == JsonValueKind.Object)
                {
                    foreach (var cidr in ExtractIpv4Cidrs(property))
                    {
                        yield return cidr;
                    }
                }
            }
        }

        private static string? BuildIpv4Cidr(string? value, JsonElement context)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (TryParseCidr(trimmed, out _, out _))
            {
                return trimmed;
            }

            if (!IPAddress.TryParse(trimmed, out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                !IsPrivateIPv4(address))
            {
                return null;
            }

            var prefixLength = ReadPrefixLength(context) ?? ReadNetmaskPrefixLength(context) ?? 24;
            return $"{address}/{prefixLength}";
        }

        private static int? ReadPrefixLength(JsonElement element)
        {
            foreach (var name in new[] { "prefix", "prefix_length", "prefixLength", "subnet_prefix", "network_prefix", "cidr_prefix" })
            {
                if (!element.TryGetProperty(name, out var property))
                {
                    continue;
                }

                var prefixLength = property.ValueKind switch
                {
                    JsonValueKind.Number when property.TryGetInt32(out var value) => value,
                    JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
                    _ => 0
                };

                if (prefixLength is > 0 and <= 32)
                {
                    return prefixLength;
                }
            }

            return null;
        }

        private static int? ReadNetmaskPrefixLength(JsonElement element)
        {
            foreach (var name in new[] { "netmask", "subnet_mask", "mask" })
            {
                var mask = GetJsonString(element, name);
                if (string.IsNullOrWhiteSpace(mask) || !IPAddress.TryParse(mask, out var address))
                {
                    continue;
                }

                var prefixLength = PrefixLengthFromNetmask(address);
                if (prefixLength is not null)
                {
                    return prefixLength;
                }
            }

            return null;
        }

        private static int? PrefixLengthFromNetmask(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }

            var value = AddressToUInt32(address);
            var prefixLength = 0;
            var seenZero = false;
            for (var bit = 31; bit >= 0; bit--)
            {
                var isSet = (value & (1u << bit)) != 0;
                if (isSet && seenZero)
                {
                    return null;
                }

                if (isSet)
                {
                    prefixLength++;
                }
                else
                {
                    seenZero = true;
                }
            }

            return prefixLength is > 0 and <= 32 ? prefixLength : null;
        }

        private static bool IsExplicitlyFalse(JsonElement element, string name) =>
            element.TryGetProperty(name, out var property) && property.ValueKind switch
            {
                JsonValueKind.False => true,
                JsonValueKind.String => bool.TryParse(property.GetString(), out var value) && !value,
                _ => false
            };

        private static bool IsContainerInterfaceName(string name) =>
            name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("br-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("hassio", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("lo", StringComparison.OrdinalIgnoreCase);

        private static async Task<IReadOnlyList<ProbeHost>> BuildTailnetHostsAsync(CancellationToken cancellationToken)
        {
            var result = await RunCommandAsync("tailscale", ["status", "--json"], TimeSpan.FromSeconds(3), cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return [];
            }

            try
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
                {
                    return [];
                }

                return peers.EnumerateObject()
                    .Select(peer => BuildTailnetHost(peer.Value))
                    .Where(host => host is not null)
                    .Cast<ProbeHost>()
                    .Take(96)
                    .ToArray();
            }
            catch
            {
                return [];
            }
        }

        private static ProbeHost? BuildTailnetHost(JsonElement peer)
        {
            if (!peer.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ip = ips.EnumerateArray()
                .Select(item => item.GetString())
                .FirstOrDefault(value => IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork);
            if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var parsed))
            {
                return null;
            }

            var displayName = peer.TryGetProperty("HostName", out var hostName) ? hostName.GetString() : null;
            return new ProbeHost(ip, parsed, ip, "Tailnet", ip, displayName, false, true, ApprovedPorts);
        }

        private static async Task<HashSet<string>> LoadLanNeighbourAddressesAsync(CancellationToken cancellationToken)
        {
            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = await RunCommandAsync("ip", ["neigh", "show"], TimeSpan.FromSeconds(3), cancellationToken);
            if (result.ExitCode == 0)
            {
                foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length == 0 ||
                        !IPAddress.TryParse(tokens[0], out var address) ||
                        address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivateIPv4(address) ||
                        tokens.Any(token => token.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
                                            token.Equals("INCOMPLETE", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    addresses.Add(address.ToString());
                }
            }

            foreach (var address in await LoadProcNetArpAddressesAsync(cancellationToken))
            {
                addresses.Add(address);
            }

            return addresses;
        }

        private static async Task<IReadOnlyList<string>> LoadProcNetArpAddressesAsync(CancellationToken cancellationToken)
        {
            const string arpPath = "/proc/net/arp";
            if (!File.Exists(arpPath))
            {
                return [];
            }

            try
            {
                var addresses = new List<string>();
                var lines = await File.ReadAllLinesAsync(arpPath, cancellationToken);
                foreach (var line in lines.Skip(1))
                {
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length < 4 ||
                        !IPAddress.TryParse(tokens[0], out var address) ||
                        address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivateIPv4(address) ||
                        tokens[3].Equals("00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    addresses.Add(address.ToString());
                }

                return addresses;
            }
            catch
            {
                return [];
            }
        }
    }

    private sealed class HomeAssistantDiscoveryAdapter(DiscoverySettings settings) : IDiscoveryAdapter
    {
        public string Name => "Home Assistant discovery";
        public bool IsEnabled(LocalHttpServiceDiscoveryRequest request) => settings.HasSupervisorToken;

        public async Task<IReadOnlyList<DiscoveryEvidence>> DiscoverAsync(
            LocalHttpServiceDiscoveryRequest request,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (!settings.HasSupervisorToken)
            {
                return [];
            }

            using var client = BuildSupervisorClient(settings.SupervisorToken);
            var evidence = new List<DiscoveryEvidence>();
            var coreInfo = await ReadSupervisorJsonAsync(client, "/core/info", cancellationToken);
            var coreEvidence = coreInfo is null ? null : BuildCoreEvidence(coreInfo.Value);
            if (coreEvidence is not null)
            {
                evidence.Add(coreEvidence);
            }

            var addons = await ReadSupervisorJsonAsync(client, "/addons", cancellationToken);
            foreach (var addon in EnumerateSupervisorAddons(addons))
            {
                var slug = GetJsonString(addon, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                {
                    continue;
                }

                var info = await ReadSupervisorJsonAsync(client, $"/addons/{Uri.EscapeDataString(slug)}/info", cancellationToken);
                evidence.AddRange(BuildAddonEvidence(slug, addon, info));
            }

            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Home Assistant discovery found {evidence.Count} named candidate(s).",
                evidence.Count,
                evidence.Count,
                evidence.Count));
            return evidence;
        }

        private static DiscoveryEvidence? BuildCoreEvidence(JsonElement payload)
        {
            var data = payload.TryGetProperty("data", out var infoData) ? infoData : payload;
            var reportedPort = GetJsonInt(data, "port");
            var port = reportedPort is > 0 and < 65536 ? reportedPort.Value : 8123;
            var ssl = GetJsonBool(data, "ssl");
            var scheme = ssl ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var notes = new List<string>
            {
                "Supervisor API identified Home Assistant Core.",
                $"Supervisor reports Home Assistant Core on {scheme}://homeassistant:{port}."
            };

            var internalIp = GetJsonString(data, "ip_address");
            if (!string.IsNullOrWhiteSpace(internalIp))
            {
                notes.Add($"Supervisor internal Docker IP: {internalIp.Trim()}.");
            }

            return new DiscoveryEvidence(
                Adapter: "Home Assistant discovery",
                Scope: "Home Assistant",
                Host: "homeassistant",
                Port: port,
                Scheme: scheme,
                ServiceName: "Home Assistant",
                ServiceKind: "home-assistant",
                Confidence: 98,
                Exposure: DiscoveryExposure.Publishable,
                Reachable: false,
                Fingerprint: $"ha-supervisor-core:{scheme}:{port}",
                DisplayName: "Home Assistant Core",
                Notes: notes);
        }

        private static IEnumerable<JsonElement> EnumerateSupervisorAddons(JsonElement? payload)
        {
            if (payload is null ||
                !payload.Value.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("addons", out var addons) ||
                addons.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var addon in addons.EnumerateArray())
            {
                yield return addon;
            }
        }

        private static IEnumerable<DiscoveryEvidence> BuildAddonEvidence(string slug, JsonElement addon, JsonElement? infoPayload)
        {
            var data = infoPayload is { } payload && payload.TryGetProperty("data", out var infoData) ? infoData : addon;
            var name = FirstNonBlank(GetJsonString(data, "name"), GetJsonString(addon, "name"), slug) ?? slug;
            var state = FirstNonBlank(GetJsonString(data, "state"), GetJsonString(addon, "state")) ?? string.Empty;
            if (!state.Equals("started", StringComparison.OrdinalIgnoreCase) && !state.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            var ingress = GetJsonBool(data, "ingress");
            var exposedPorts = ExtractSupervisorPorts(data).ToArray();
            foreach (var port in exposedPorts)
            {
                yield return new DiscoveryEvidence(
                    Adapter: "Home Assistant discovery",
                    Scope: "Home Assistant",
                    Host: "homeassistant",
                    Port: port.Port,
                    Scheme: GuessSchemeFromPort(port.Port),
                    ServiceName: name,
                    ServiceKind: FingerprintRules.NormalizeServiceKind(name, slug),
                    Confidence: 84,
                    Exposure: DiscoveryExposure.RequiresManualConfirmation,
                    Reachable: false,
                    Fingerprint: $"ha-addon:{slug}:{port.Port}",
                    DisplayName: $"{name} add-on",
                    Notes: [$"Supervisor add-on slug {slug}.", $"State: {state}.", ingress ? "Ingress is enabled." : "Ingress is not enabled.", port.Description]);
            }

            var ingressPort = GetJsonInt(data, "ingress_port");
            if (ingress && exposedPorts.Length == 0 && ingressPort is > 0)
            {
                yield return new DiscoveryEvidence(
                    Adapter: "Home Assistant discovery",
                    Scope: "Home Assistant",
                    Host: "homeassistant",
                    Port: ingressPort.Value,
                    Scheme: GuessSchemeFromPort(ingressPort.Value),
                    ServiceName: name,
                    ServiceKind: FingerprintRules.NormalizeServiceKind(name, slug),
                    Confidence: 72,
                    Exposure: DiscoveryExposure.InternalOnly,
                    Reachable: false,
                    Fingerprint: $"ha-addon-ingress:{slug}:{ingressPort.Value}",
                    DisplayName: $"{name} add-on",
                    Notes: [$"Supervisor add-on slug {slug}.", $"State: {state}.", $"Ingress port {ingressPort.Value}.", "No exposed host port was reported."]);
            }
        }

        private static IEnumerable<(int Port, string Description)> ExtractSupervisorPorts(JsonElement data)
        {
            if (!data.TryGetProperty("ports", out var ports) || ports.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var property in ports.EnumerateObject())
            {
                var hostPort = property.Value.ValueKind switch
                {
                    JsonValueKind.Number when property.Value.TryGetInt32(out var number) => number,
                    JsonValueKind.String when int.TryParse(property.Value.GetString(), out var number) => number,
                    _ => 0
                };

                if (hostPort > 0)
                {
                    yield return (hostPort, $"{property.Name} is exposed as host port {hostPort}.");
                }
            }
        }
    }

    private sealed partial class DockerDiscoveryAdapter(DiscoverySettings settings) : IDiscoveryAdapter
    {
        private const string DockerSocketPath = "/var/run/docker.sock";
        public string Name => "Docker discovery";
        public bool IsEnabled(LocalHttpServiceDiscoveryRequest request) => request.IncludeDocker && settings.EnableDockerDiscovery;

        public async Task<IReadOnlyList<DiscoveryEvidence>> DiscoverAsync(
            LocalHttpServiceDiscoveryRequest request,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(DockerSocketPath))
            {
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    "Docker discovery skipped. Docker API socket is not available to this add-on.",
                    0,
                    0,
                    0));
                return [];
            }

            var evidence = new List<DiscoveryEvidence>();
            try
            {
                using var client = BuildDockerApiClient();
                using var response = await client.GetAsync("/containers/json", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                foreach (var container in document.RootElement.EnumerateArray())
                {
                    var containerName = FirstNonBlank(ParseContainerNames(container).FirstOrDefault(), GetJsonString(container, "Id")) ?? "Docker container";
                    var image = GetJsonString(container, "Image") ?? string.Empty;
                    var labels = ReadDockerLabels(container);
                    var labelText = string.Join(" ", labels.Keys.Concat(labels.Values));
                    var networks = ReadDockerNetworks(container);
                    var serviceKind = FingerprintRules.NormalizeServiceKind(containerName, image, labelText);

                    foreach (var port in ParseDockerApiPorts(container).Take(128))
                    {
                        var host = port.HostAddress is "0.0.0.0" or "::" ? "localhost" : port.HostAddress;
                        evidence.Add(new DiscoveryEvidence(
                            Adapter: Name,
                            Scope: "Docker",
                            Host: host,
                            Port: port.HostPort,
                            Scheme: GuessSchemeFromPort(port.HostPort),
                            ServiceName: FingerprintRules.BuildFriendlyName(serviceKind, containerName),
                            ServiceKind: serviceKind,
                            Confidence: 82,
                            Exposure: FingerprintRules.ClassifyExposure(serviceKind, 82),
                            Reachable: false,
                            Fingerprint: $"docker:{containerName}:{port.HostPort}:{serviceKind}",
                            DisplayName: containerName,
                            Notes:
                            [
                                $"Docker image: {image}",
                                $"Published port: {host}:{port.HostPort}->{port.ContainerPort}/tcp",
                                networks.Count == 0 ? "Docker networks were not reported." : $"Docker networks: {string.Join(", ", networks)}.",
                                labels.Count == 0 ? "Docker labels were not reported." : $"Docker label keys: {string.Join(", ", labels.Keys.Take(8))}."
                            ]));
                    }
                }
            }
            catch
            {
                return [];
            }

            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Docker discovery found {evidence.Count} published port candidate(s) from the Docker API.",
                evidence.Count,
                evidence.Count,
                evidence.Count));
            return evidence;
        }

        private static HttpClient BuildDockerApiClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(DockerSocketPath), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            return new HttpClient(handler) { BaseAddress = new Uri("http://docker"), Timeout = TimeSpan.FromSeconds(5) };
        }

        private static IEnumerable<string> ParseContainerNames(JsonElement container)
        {
            if (!container.TryGetProperty("Names", out var names) || names.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in names.EnumerateArray())
            {
                var name = item.GetString()?.Trim().Trim('/');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name;
                }
            }
        }

        private static IReadOnlyDictionary<string, string> ReadDockerLabels(JsonElement container)
        {
            if (!container.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return labels.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> ReadDockerNetworks(JsonElement container)
        {
            if (!container.TryGetProperty("NetworkSettings", out var networkSettings) ||
                !networkSettings.TryGetProperty("Networks", out var networks) ||
                networks.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return networks.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IEnumerable<DockerPublishedPort> ParseDockerApiPorts(JsonElement container)
        {
            if (!container.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var port in ports.EnumerateArray())
            {
                var type = GetJsonString(port, "Type") ?? "tcp";
                if (!type.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hostPort = GetJsonInt(port, "PublicPort") ?? 0;
                var containerPort = GetJsonInt(port, "PrivatePort") ?? 0;
                if (hostPort > 0 && containerPort > 0)
                {
                    yield return new DockerPublishedPort(
                        FirstNonBlank(GetJsonString(port, "IP"), "localhost") ?? "localhost",
                        hostPort,
                        containerPort);
                }
            }
        }

        private sealed record DockerPublishedPort(string HostAddress, int HostPort, int ContainerPort);
    }

    private static class DiscoveryCorrelator
    {
        public static IReadOnlyList<LocalHttpServiceEndpoint> Correlate(IReadOnlyList<DiscoveryEvidence> evidence)
        {
            var lanEvidence = evidence
                .Where(item => item.Reachable)
                .ToArray();
            var namedEvidence = evidence
                .Where(item => !item.Reachable)
                .ToArray();
            var enriched = new List<DiscoveryEvidence>(lanEvidence);

            foreach (var named in namedEvidence)
            {
                var match = lanEvidence.FirstOrDefault(lan =>
                    lan.Port == named.Port &&
                    (lan.ServiceKind.Equals(named.ServiceKind, StringComparison.OrdinalIgnoreCase) ||
                     lan.Host.Equals(named.Host, StringComparison.OrdinalIgnoreCase)));
                enriched.Add(match is null
                    ? named
                    : named with
                    {
                        Host = match.Host,
                        Scheme = match.Scheme,
                        Reachable = true,
                        Title = match.Title,
                        ServerHeader = match.ServerHeader,
                        TlsSubject = match.TlsSubject,
                        FaviconHash = match.FaviconHash,
                        RedirectLocation = match.RedirectLocation,
                        StatusCode = match.StatusCode,
                        IpAddress = match.IpAddress,
                        Confidence = Math.Min(99, Math.Max(named.Confidence, match.Confidence) + 8),
                        Fingerprint = FirstNonBlank(match.Fingerprint, named.Fingerprint) ?? named.Fingerprint,
                        Notes = (named.Notes ?? [])
                            .Concat(match.Notes ?? [])
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    });
            }

            return enriched
                .GroupBy(item => $"{item.Scheme}|{FirstNonBlank(item.IpAddress, item.Host)}|{item.Port}|{item.Fingerprint}", StringComparer.OrdinalIgnoreCase)
                .Select(group => ToEndpoint(group.ToArray()))
                .Where(endpoint => endpoint.Port > 0)
                .ToArray();
        }

        public static LocalHttpServiceEndpoint ToEndpoint(IReadOnlyList<DiscoveryEvidence> group)
        {
            var best = group
                .OrderByDescending(item => item.Adapter.Equals("Home Assistant discovery", StringComparison.OrdinalIgnoreCase) ? 2 : item.Adapter.Equals("Docker discovery", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(item => item.Confidence)
                .First();
            var confidence = Math.Min(99, group.Max(item => item.Confidence) + Math.Max(0, group.Select(item => item.Adapter).Distinct(StringComparer.OrdinalIgnoreCase).Count() - 1) * 5);
            if (!group.Any(item => item.Reachable))
            {
                confidence = Math.Min(confidence, 88);
            }

            var exposure = group.Any(item => item.Exposure == DiscoveryExposure.UnsafeToExpose)
                ? DiscoveryExposure.UnsafeToExpose
                : group.Any(item => item.Exposure == DiscoveryExposure.RequiresManualConfirmation)
                    ? DiscoveryExposure.RequiresManualConfirmation
                    : group.Any(item => item.Exposure == DiscoveryExposure.InternalOnly)
                        ? DiscoveryExposure.InternalOnly
                        : DiscoveryExposure.Publishable;
            if (exposure == DiscoveryExposure.Publishable && !group.Any(item => item.Reachable))
            {
                exposure = DiscoveryExposure.RequiresManualConfirmation;
            }

            var displayName = FirstNonBlank(best.ServiceName, $"{best.Host}:{best.Port}") ?? $"{best.Host}:{best.Port}";
            return new LocalHttpServiceEndpoint(
                BuildUrl(best.Scheme, best.Host, best.Port),
                best.Scheme,
                best.Host,
                best.Port,
                best.StatusCode,
                best.Title,
                best.ServerHeader,
                best.Scope,
                best.IpAddress,
                displayName,
                DateTimeOffset.UtcNow,
                confidence,
                best.ServiceName,
                best.ServiceKind,
                exposure,
                FirstNonBlank(best.Fingerprint, $"{best.ServiceKind}:{best.Port}") ?? $"{best.ServiceKind}:{best.Port}",
                group.SelectMany(item => item.Notes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private static class HttpFingerprintProbe
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly ConcurrentDictionary<string, Task<string?>> ReverseLookupTasks = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<DiscoveryEvidence?> ProbeAsync(ProbeHost host, int port, string scheme, CancellationToken cancellationToken)
        {
            var probeUrl = BuildUrl(scheme, host.ProbeAddressName, port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{probeUrl}/");
                request.Headers.UserAgent.ParseAdd("LinuxMadeSane-capability-discovery");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var title = await TryReadTitleAsync(response, timeout.Token);
                var redirect = response.Headers.Location?.ToString() ?? string.Empty;
                var server = response.Headers.Server.ToString();
                var faviconHash = await TryReadFaviconHashAsync(Client, probeUrl, timeout.Token);
                var tlsSubject = scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? await TryReadTlsSubjectAsync(host.ProbeAddressName, port, timeout.Token)
                    : string.Empty;
                var displayHost = await ResolveHostNameAsync(host, cancellationToken);
                var fingerprint = FingerprintRules.Fingerprint(title, server, redirect, faviconHash, tlsSubject, port);
                var notes = displayHost.Equals(host.TargetHost, StringComparison.OrdinalIgnoreCase)
                    ? fingerprint.Notes
                    : fingerprint.Notes.Concat([$"DNS hostname: {displayHost}."]).ToArray();

                return new DiscoveryEvidence(
                    Adapter: "LAN discovery",
                    Scope: host.Scope,
                    Host: displayHost,
                    Port: port,
                    Scheme: scheme,
                    ServiceName: fingerprint.Name,
                    ServiceKind: fingerprint.Kind,
                    Confidence: fingerprint.Confidence,
                    Exposure: host.Scope.Equals("Localhost", StringComparison.OrdinalIgnoreCase)
                        ? DiscoveryExposure.InternalOnly
                        : fingerprint.Exposure,
                    Reachable: true,
                    Fingerprint: fingerprint.Fingerprint,
                    StatusCode: (int)response.StatusCode,
                    Title: title,
                    ServerHeader: server,
                    RedirectLocation: redirect,
                    TlsSubject: tlsSubject,
                    FaviconHash: faviconHash,
                    DisplayName: host.DisplayName,
                    IpAddress: host.IpAddress,
                    Notes: notes);
            }
            catch
            {
                return null;
            }
        }

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                MaxConnectionsPerServer = 512,
                ConnectTimeout = ConnectTimeout,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true
                }
            };

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private static readonly TimeSpan ReverseLookupTimeout = TimeSpan.FromSeconds(2);

        private static async Task<string> ResolveHostNameAsync(ProbeHost host, CancellationToken cancellationToken)
        {
            if (host.ProbeAddress is null || !IPAddress.TryParse(host.TargetHost, out _))
            {
                return host.TargetHost;
            }

            var resolvedNameTask = ReverseLookupTasks.GetOrAdd(
                host.ProbeAddress.ToString(),
                _ => ResolveHostNameCoreAsync(host.ProbeAddress));
            var resolvedName = await resolvedNameTask.WaitAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(resolvedName) ? host.TargetHost : resolvedName;
        }

        private static async Task<string?> ResolveHostNameCoreAsync(IPAddress address)
        {
            var dnsName = await TryResolveHostNameAsync(address, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(dnsName))
            {
                return dnsName;
            }

            return await TryResolveHostNameWithGetentAsync(address, CancellationToken.None);
        }

        private static async Task<string?> TryResolveHostNameAsync(IPAddress address, CancellationToken cancellationToken)
        {
            try
            {
                var lookupTask = Dns.GetHostEntryAsync(address);
                var completedTask = await Task.WhenAny(lookupTask, Task.Delay(ReverseLookupTimeout, cancellationToken));
                if (!ReferenceEquals(completedTask, lookupTask))
                {
                    return null;
                }

                var entry = await lookupTask;
                return NormalizeResolvedHostName(entry.HostName);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> TryResolveHostNameWithGetentAsync(IPAddress address, CancellationToken cancellationToken)
        {
            var result = await RunCommandAsync("getent", ["hosts", address.ToString()], ReverseLookupTimeout, cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals(address.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalized = NormalizeResolvedHostName(parts[1]);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            return null;
        }

        private static string? NormalizeResolvedHostName(string? value)
        {
            var name = value?.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _)
                ? null
                : name;
        }
    }

    private static class FingerprintRules
    {
        public static FingerprintResult Fingerprint(string? title, string? server, string? redirect, string? faviconHash, string? tlsSubject, int port)
        {
            var haystack = $"{title} {server} {redirect} {faviconHash} {tlsSubject}".ToLowerInvariant();
            return haystack switch
            {
                var text when text.Contains("home assistant", StringComparison.Ordinal) =>
                    Known("Home Assistant", "home-assistant", 99, DiscoveryExposure.Publishable, "home-assistant"),
                var text when text.Contains("portainer", StringComparison.Ordinal) =>
                    Known("Portainer", "portainer", 98, DiscoveryExposure.RequiresManualConfirmation, "portainer"),
                var text when text.Contains("jellyfin", StringComparison.Ordinal) =>
                    Known("Jellyfin", "jellyfin", 94, DiscoveryExposure.Publishable, "jellyfin"),
                var text when text.Contains("plex", StringComparison.Ordinal) =>
                    Known("Plex", "plex", 94, DiscoveryExposure.Publishable, "plex"),
                var text when text.Contains("grafana", StringComparison.Ordinal) =>
                    Known("Grafana", "grafana", 90, DiscoveryExposure.RequiresManualConfirmation, "grafana"),
                var text when text.Contains("adguard", StringComparison.Ordinal) =>
                    Known("AdGuard Home", "adguard-home", 90, DiscoveryExposure.RequiresManualConfirmation, "adguard"),
                var text when text.Contains("uptime kuma", StringComparison.Ordinal) =>
                    Known("Uptime Kuma", "uptime-kuma", 90, DiscoveryExposure.Publishable, "uptime-kuma"),
                var text when text.Contains("docker", StringComparison.Ordinal) && port is 2375 or 2376 =>
                    Known("Docker API", "docker-api", 99, DiscoveryExposure.UnsafeToExpose, "docker-api"),
                _ when port == 8123 =>
                    PortHint("Home Assistant", "home-assistant", 76, DiscoveryExposure.RequiresManualConfirmation, "home-assistant-port"),
                _ => Unknown(port)
            };
        }

        public static string NormalizeServiceKind(params string?[] values)
        {
            var text = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
            if (text.Contains("home assistant", StringComparison.Ordinal)) return "home-assistant";
            if (text.Contains("portainer", StringComparison.Ordinal)) return "portainer";
            if (text.Contains("jellyfin", StringComparison.Ordinal)) return "jellyfin";
            if (text.Contains("plex", StringComparison.Ordinal)) return "plex";
            if (text.Contains("grafana", StringComparison.Ordinal)) return "grafana";
            if (text.Contains("adguard", StringComparison.Ordinal)) return "adguard-home";
            if (text.Contains("uptime kuma", StringComparison.Ordinal)) return "uptime-kuma";
            if (text.Contains("docker", StringComparison.Ordinal)) return "docker-api";
            return "unknown-http";
        }

        public static string BuildFriendlyName(string serviceKind, string fallback) => serviceKind switch
        {
            "home-assistant" => "Home Assistant",
            "portainer" => "Portainer",
            "jellyfin" => "Jellyfin",
            "plex" => "Plex",
            "grafana" => "Grafana",
            "adguard-home" => "AdGuard Home",
            "uptime-kuma" => "Uptime Kuma",
            "docker-api" => "Docker API",
            _ => string.IsNullOrWhiteSpace(fallback) ? "unknown HTTP service" : fallback
        };

        public static DiscoveryExposure ClassifyExposure(string serviceKind, int confidence) => serviceKind switch
        {
            "docker-api" => DiscoveryExposure.UnsafeToExpose,
            "portainer" or "grafana" or "adguard-home" => DiscoveryExposure.RequiresManualConfirmation,
            "unknown-http" => DiscoveryExposure.RequiresManualConfirmation,
            _ => confidence >= 80 ? DiscoveryExposure.Publishable : DiscoveryExposure.RequiresManualConfirmation
        };

        private static FingerprintResult Known(string name, string kind, int confidence, DiscoveryExposure exposure, string fingerprint) =>
            new(name, kind, confidence, exposure, fingerprint, [$"{name} fingerprint matched from HTTP/TLS metadata."]);

        private static FingerprintResult PortHint(string name, string kind, int confidence, DiscoveryExposure exposure, string fingerprint) =>
            new(name, kind, confidence, exposure, fingerprint, [$"{name} common port matched without response metadata."]);

        private static FingerprintResult Unknown(int port) =>
            new("unknown HTTP service", "unknown-http", 45, DiscoveryExposure.RequiresManualConfirmation, $"unknown:{port}", ["HTTP/S response found, but no known service fingerprint matched."]);
    }

    private sealed record DiscoveryEvidence(
        string Adapter,
        string Scope,
        string Host,
        int Port,
        string Scheme,
        string ServiceName,
        string ServiceKind,
        int Confidence,
        DiscoveryExposure Exposure,
        bool Reachable,
        string Fingerprint,
        int StatusCode = 0,
        string? Title = null,
        string? ServerHeader = null,
        string? RedirectLocation = null,
        string? TlsSubject = null,
        string? FaviconHash = null,
        string? DisplayName = null,
        string? IpAddress = null,
        IReadOnlyList<string>? Notes = null);

    private sealed record FingerprintResult(
        string Name,
        string Kind,
        int Confidence,
        DiscoveryExposure Exposure,
        string Fingerprint,
        IReadOnlyList<string> Notes);

    private sealed record ProbeHost(
        string ProbeAddressName,
        IPAddress? ProbeAddress,
        string TargetHost,
        string Scope,
        string? IpAddress,
        string? DisplayName,
        bool IsLocalProbe,
        bool IsKnownLive,
        IReadOnlyList<int> KnownPorts);

    private sealed record LocalInterfaceSubnet(IPAddress Address, IPAddress Mask);

    private sealed record DiscoverySettings(
        bool EnableDockerDiscovery,
        bool EnableExpandedLanDiscovery,
        IReadOnlyList<string> Cidrs,
        IReadOnlyList<int> AdditionalPorts,
        string SupervisorToken)
    {
        public bool HasSupervisorToken => !string.IsNullOrWhiteSpace(SupervisorToken);

        public static DiscoverySettings Load(EdgeGatewayCoreOptions options)
        {
            var token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN") ?? string.Empty;
            var enableDocker = options.EnableDockerDiscovery;
            var enableExpanded = options.EnableExpandedLanDiscovery;
            var cidrs = options.DiscoveryCidrs.ToList();
            var ports = options.DiscoveryPorts.ToList();

            try
            {
                if (File.Exists(options.OptionsJsonPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(options.OptionsJsonPath));
                    var root = document.RootElement;
                    enableDocker = GetBool(root, "advanced_docker_discovery") || GetBool(root, "enable_docker_discovery") || enableDocker;
                    enableExpanded = GetBool(root, "expanded_lan_discovery") || GetBool(root, "enable_expanded_lan_discovery") || enableExpanded;
                    cidrs.AddRange(GetStringList(root, "discovery_cidrs"));
                    ports.AddRange(GetIntList(root, "discovery_ports"));
                }
            }
            catch
            {
            }

            return new DiscoverySettings(
                enableDocker,
                enableExpanded,
                cidrs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ports.Where(port => port is > 0 and < 65536).Distinct().ToArray(),
                token);
        }
    }

    private sealed class DiscoveryProgressState(int totalProbeCount)
    {
        private int probedCount;
        private int foundCount;
        public int TotalProbeCount { get; } = totalProbeCount;
        public int ProbedCount => Volatile.Read(ref probedCount);
        public int FoundCount => Volatile.Read(ref foundCount);
        public int IncrementProbedCount() => Interlocked.Increment(ref probedCount);
        public int IncrementFoundCount() => Interlocked.Increment(ref foundCount);
    }

    private sealed class HostDiscoveryProgressState(int totalHostCount)
    {
        private int checkedCount;
        private int liveCount;
        private int foundCount;
        public int TotalHostCount { get; } = totalHostCount;
        public int CheckedCount => Volatile.Read(ref checkedCount);
        public int LiveCount => Volatile.Read(ref liveCount);
        public int FoundCount => Volatile.Read(ref foundCount);
        public int IncrementCheckedCount() => Interlocked.Increment(ref checkedCount);
        public int IncrementLiveCount() => Interlocked.Increment(ref liveCount);
        public int IncrementFoundCount() => Interlocked.Increment(ref foundCount);
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private static IReadOnlyList<int> BuildPortList(DiscoverySettings settings) =>
        ApprovedPorts
            .Concat(settings.EnableExpandedLanDiscovery ? ExpandedPorts : [])
            .Concat(settings.EnableExpandedLanDiscovery ? settings.AdditionalPorts : [])
            .Distinct()
            .Order()
            .ToArray();

    private static IEnumerable<string> GuessSchemes(int port)
    {
        if (port is 443 or 5001 or 8443 or 9443 or 10443)
        {
            yield return Uri.UriSchemeHttps;
            yield return Uri.UriSchemeHttp;
        }
        else
        {
            yield return Uri.UriSchemeHttp;
            yield return Uri.UriSchemeHttps;
        }
    }

    private static string GuessSchemeFromPort(int port) =>
        port is 443 or 5001 or 8443 or 9443 or 10443 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

    private static async Task<bool> CanOpenTcpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanOpenAnyTcpAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = ports
            .Distinct()
            .Select(port => CanOpenTcpWithLimitAsync(address, port, tcpConcurrency, linked.Token))
            .ToList();

        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);
                if (await completed)
                {
                    linked.Cancel();
                    return true;
                }
            }

            return false;
        }
        finally
        {
            linked.Cancel();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
            }
        }
    }

    private static async Task<bool> CanOpenTcpWithLimitAsync(
        IPAddress address,
        int port,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken)
    {
        try
        {
            await tcpConcurrency.WaitAsync(cancellationToken);
            try
            {
                return await CanOpenTcpAsync(address, port, cancellationToken);
            }
            finally
            {
                tcpConcurrency.Release();
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanPingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var pingTask = ping.SendPingAsync(address, 650);
            var completedTask = await Task.WhenAny(pingTask, Task.Delay(700, cancellationToken));
            if (!ReferenceEquals(completedTask, pingTask))
            {
                return false;
            }

            var reply = await pingTask;
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> TryReadTitleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > 128_000)
        {
            return null;
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16_384];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var match = TitleRegex().Match(text);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryReadFaviconHashAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"{baseUrl}/favicon.ico", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length == 0 || bytes.Length > 128_000
                ? string.Empty
                : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> TryReadTlsSubjectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, timeout.Token);
            using var ssl = new SslStream(client.GetStream(), false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(host, null, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
            return ssl.RemoteCertificate is null ? string.Empty : new X509Certificate2(ssl.RemoteCertificate).Subject;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<IPAddress> ExpandLocalInterfaceCidrs()
    {
        var interfaces = GetLocalIPv4Interfaces();
        var result = new List<IPAddress>();
        foreach (var localInterface in interfaces)
        {
            var addressValue = AddressToUInt32(localInterface.Address);
            var maskValue = AddressToUInt32(localInterface.Mask);
            var networkValue = addressValue & maskValue;
            var broadcastValue = networkValue | ~maskValue;
            var availableHosts = broadcastValue > networkValue ? broadcastValue - networkValue - 1 : 0;

            for (var offset = 1u; offset <= availableHosts; offset++)
            {
                var candidate = UInt32ToAddress(networkValue + offset);
                if (!candidate.Equals(localInterface.Address))
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static IReadOnlyList<IPAddress> ExpandCidrs(IReadOnlyList<string> cidrs)
    {
        var result = new List<IPAddress>();
        foreach (var cidr in cidrs)
        {
            if (!TryParseCidr(cidr, out var network, out var prefixLength))
            {
                continue;
            }

            var hostCount = Math.Max(0, (1 << Math.Clamp(32 - prefixLength, 0, 16)) - 2);
            var baseValue = AddressToUInt32(network) & MaskForPrefix(prefixLength);
            for (var offset = 1; offset <= hostCount; offset++)
            {
                result.Add(UInt32ToAddress(baseValue + (uint)offset));
            }
        }

        return result;
    }

    private static bool IsPrivateCidr(string cidr)
    {
        if (!TryParseCidr(cidr, out var address, out _))
        {
            return false;
        }

        return IsPrivateIPv4(address);
    }

    private static bool TryParseCidr(string cidr, out IPAddress network, out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var parsedNetwork) ||
            parsedNetwork.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var parsedPrefixLength) ||
            parsedPrefixLength is < 16 or > 30)
        {
            return false;
        }

        network = parsedNetwork;
        prefixLength = parsedPrefixLength;
        return true;
    }

    private static uint MaskForPrefix(int prefixLength) =>
        prefixLength <= 0 ? 0 : uint.MaxValue << (32 - prefixLength);

    private static IReadOnlyList<LocalInterfaceSubnet> GetLocalIPv4Interfaces()
    {
        var interfaces = new List<LocalInterfaceSubnet>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var addressInfo in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(addressInfo.Address))
                {
                    continue;
                }

                var mask = addressInfo.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any))
                {
                    var bytes = addressInfo.Address.GetAddressBytes();
                    mask = new IPAddress([255, 255, 255, 0]);
                    if (bytes[0] == 10)
                    {
                        mask = new IPAddress([255, 0, 0, 0]);
                    }
                    else if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    {
                        mask = new IPAddress([255, 240, 0, 0]);
                    }
                }

                interfaces.Add(new LocalInterfaceSubnet(addressInfo.Address, mask));
            }
        }

        return interfaces
            .DistinctBy(item => $"{item.Address}/{item.Mask}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static uint AddressToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UInt32ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static string BuildUrl(string scheme, string host, int port) =>
        $"{scheme}://{FormatHostForUrl(host)}:{port}";

    private static string FormatHostForUrl(string host) =>
        host.Contains(':', StringComparison.Ordinal) && IPAddress.TryParse(host.Trim('[', ']'), out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host.Trim('[', ']')}]"
            : host;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? GetJsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool GetJsonBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var value) && value,
            _ => false
        };

    private static int? GetJsonInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var value) && value,
            _ => false
        };

    private static IEnumerable<string> GetStringList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            yield break;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            foreach (var item in property.GetString()?.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            {
                yield return item;
            }
        }
        else if (property.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in property.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                yield return item!;
            }
        }
    }

    private static IEnumerable<int> GetIntList(JsonElement element, string name)
    {
        foreach (var item in GetStringList(element, name))
        {
            if (int.TryParse(item, out var port))
            {
                yield return port;
            }
        }
    }

    private static async Task<CommandResult> RunCommandAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch
        {
            return new CommandResult(-1, string.Empty, string.Empty);
        }
    }

    private sealed class IPAddressComparer : IEqualityComparer<IPAddress>
    {
        public static IPAddressComparer Instance { get; } = new();
        public bool Equals(IPAddress? x, IPAddress? y) => Equals(x?.ToString(), y?.ToString());
        public int GetHashCode(IPAddress obj) => obj.ToString().GetHashCode(StringComparison.Ordinal);
    }
}
