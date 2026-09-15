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
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed partial class LocalHttpServiceDiscoveryService(IOptions<EdgeGatewayCoreOptions> options) : ILocalHttpServiceDiscoveryService
{
    private const int MaxConcurrentCacheValidation = 256;
    // Priority HTTP/S ports probed first on every live IP (common homelab defaults).
    private static readonly int[] CommonHomelabPorts =
    [
        80, 81, 443, 1880, 1984, 2283, 3000, 3001, 5000, 5001, 5380, 5601,
        6767, 6789, 7125, 7443, 7745, 7878, 8000, 8006, 8043, 8080, 8083,
        8096, 8111, 8112, 8123, 8200, 8384, 8443, 8686, 8787, 8920, 8971,
        8989, 9000, 9001, 9090, 9091, 9443, 9696, 10000, 10443, 15672,
        18080, 19999, 32400
    ];

    private static readonly HashSet<int> CommonHomelabPortSet = new(CommonHomelabPorts);

    // Always probed on every LAN IP (not gated behind expanded_lan_discovery).
    private static readonly int[] ExpandedPorts =
    [
        82, 88, 800, 808, 8008, 8009, 2342, 5080, 7000, 7126, 8081, 8888, 11434, 50000, 50001
    ];

    private static readonly int[] HostLivenessPorts =
    [
        80, 443, 8080, 8123, 8443, 9443, 5000, 5001, 3000, 8006, 8096, 9000, 9090, 10000, 32400, 22, 53, 139, 445
    ];

    private static readonly HashSet<int> HttpsPreferredPorts =
    [
        443, 5001, 7443, 8043, 8443, 8920, 9443, 10443
    ];

    // Alternate HTTPS admin UIs often bind Nxxx443 (e.g. 8443, 10443, 11443) without advertising.
    private static readonly int[] HttpsConventionPorts = BuildHttpsConventionPorts();

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(450);
    // Aggressive LAN connect budget so dead IPs fail fast instead of SYN-flooding the network.
    private static readonly TimeSpan LanConnectTimeout = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ProbePaceDelay = TimeSpan.Zero;
    private const int MaxRedirectFollowPortsPerHost = 8;
    private const int MaxLanScanAddresses = 384;
    private readonly SemaphoreSlim cacheMutationLock = new(1, 1);

    private const int MaxFaviconBytes = 32_768;

    [GeneratedRegex("<title[^>]*>\\s*(?<title>.*?)\\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""<link[^>]+rel\s*=\s*["']?(?:shortcut\s+)?(?:apple-touch-)?icon["']?[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FaviconLinkTagRegex();

    [GeneratedRegex("""href\s*=\s*["'](?<href>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex FaviconHrefRegex();

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
                        "Docker discovery skipped. Enable advanced_docker_discovery in app options before using Docker API evidence.",
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
            // Replace every scope included in this scan with fresh probes — do not keep stale
            // fingerprints/labels from the previous cache for those scopes.
            var merged = existing
                .Where(endpoint => !requestedScopes.Contains(endpoint.Scope))
                .Concat(correlated)
                .GroupBy(BuildEndpointKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(endpoint => FingerprintRules.IsUnknownLabel(endpoint.ServiceName) ? 1 : 0)
                    .ThenByDescending(endpoint => endpoint.Confidence)
                    .ThenByDescending(endpoint => endpoint.DiscoveredAtUtc ?? DateTimeOffset.MinValue)
                    .First())
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
            .OrderBy(endpoint => LocalHttpServiceDiscoveryRanking.TitleQualityRank(endpoint.Title))
            .ThenBy(endpoint => endpoint.Exposure switch
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
        if (request.IncludeLan)
        {
            scopes.Add("LAN");
            scopes.Add("SSDP");
            scopes.Add("mDNS");
            scopes.Add("WS-Discovery");
        }
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
        $"{endpoint.Scheme}|{NormalizeEndpointAddress(endpoint)}|{endpoint.Port}";

    private static string NormalizeEndpointAddress(LocalHttpServiceEndpoint endpoint) =>
        (FirstNonBlank(endpoint.IpAddress, endpoint.Host) ?? endpoint.Host).Trim().TrimEnd('.').ToLowerInvariant();

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
        // Keep host/TCP fan-out modest — sweeping every LAN IP at 512-wide floods conntrack and
        // makes live HTTP probes time out (fewer results, not more).
        private const int MaxConcurrentHostChecks = 32;
        private const int MaxConcurrentTcpLivenessChecks = 256;
        private const int MaxConcurrentProbes = 64;
        private static readonly TimeSpan MulticastDiscoveryTimeout = TimeSpan.FromMilliseconds(1250);
        public string Name => "LAN discovery";

        public bool IsEnabled(LocalHttpServiceDiscoveryRequest request) => request.IncludeLocalhost || request.IncludeLan || request.IncludeTailnet;

        public async Task<IReadOnlyList<DiscoveryEvidence>> DiscoverAsync(
            LocalHttpServiceDiscoveryRequest request,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            var stages = new List<DiscoveryStage>();
            var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request.IncludeLocalhost)
            {
                AddStage(stages, seenHosts, "Localhost", BuildLocalhostHosts());
            }

            if (request.IncludeLan)
            {
                var scanPlan = await BuildLanScanPlanAsync(settings, cancellationToken);
                ReportLanScanPlan(scanPlan, settings, progress);
                AddStage(stages, seenHosts, "ARP cache", scanPlan.KnownHosts);
                AddStage(stages, seenHosts, "SSDP discovery", await DiscoverSsdpHostsAsync(progress, cancellationToken));
                AddStage(stages, seenHosts, "mDNS discovery", await DiscoverMdnsHostsAsync(progress, cancellationToken));
                AddStage(stages, seenHosts, "WS-Discovery", await DiscoverWsDiscoveryHostsAsync(progress, cancellationToken));
                AddStage(stages, seenHosts, "Targeted LAN scan", scanPlan.ScanHosts);
            }

            if (request.IncludeTailnet)
            {
                AddStage(stages, seenHosts, "Tailnet", await BuildTailnetHostsAsync(cancellationToken));
            }

            var portCount = BuildBaseScanPorts(settings).Count;
            var totalHosts = stages.Sum(stage => stage.Hosts.Count);
            var progressState = new HostDiscoveryProgressState(totalHosts);
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                totalHosts == 0 ? "No local discovery targets." : $"Checking {totalHosts} staged local discovery target(s); discovered hosts are probed before the wider LAN scan.",
                0,
                totalHosts,
                0));

            var results = new ConcurrentBag<DiscoveryEvidence>();
            using var hostConcurrency = new SemaphoreSlim(MaxConcurrentHostChecks);
            using var tcpConcurrency = new SemaphoreSlim(MaxConcurrentTcpLivenessChecks);
            using var serviceConcurrency = new SemaphoreSlim(MaxConcurrentProbes);
            foreach (var stage in stages)
            {
                progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                    $"{stage.Name}: sweeping {stage.Hosts.Count} address(es) across {portCount} well-known HTTP/S port(s).",
                    progressState.CheckedCount,
                    progressState.TotalHostCount,
                    progressState.FoundCount));

                await Task.WhenAll(stage.Hosts.Select(host => ProbeHostWhenReachableAsync(
                    host,
                    settings,
                    hostConcurrency,
                    tcpConcurrency,
                    serviceConcurrency,
                    results,
                    progressState,
                    progress,
                    cancellationToken)));
            }

            return results.ToArray();
        }

        private static void AddStage(
            ICollection<DiscoveryStage> stages,
            ISet<string> seenHosts,
            string name,
            IEnumerable<ProbeHost> hosts)
        {
            var distinctHosts = hosts
                .Where(host => seenHosts.Add(BuildProbeHostKey(host)))
                .ToArray();
            if (distinctHosts.Length > 0)
            {
                stages.Add(new DiscoveryStage(name, distinctHosts));
            }
        }

        private static string BuildProbeHostKey(ProbeHost host) =>
            FirstNonBlank(host.IpAddress, host.ProbeAddressName, host.TargetHost)?.Trim().TrimEnd('.').ToLowerInvariant()
            ?? host.TargetHost;

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
            IReadOnlyList<int> openPorts = [];
            await hostConcurrency.WaitAsync(cancellationToken);
            try
            {
                var candidates = BuildPortsForHost(host, settings);
                if (host.IsLocalProbe && host.ProbeAddress is null)
                {
                    openPorts = candidates;
                }
                else if (host.ProbeAddress is not null)
                {
                    // Unknown CIDR IPs: cheap liveness first (ping / any well-known port), then full
                    // port inventory. Blind full sweeps of every dead IP SYN-flood the LAN and
                    // starve real HTTP probes. Liveness uses the full candidate set so :11443-only
                    // hosts are still found — just not by hammering empty addresses first.
                    if (!host.IsKnownLive &&
                        !await IsLanHostReachableAsync(host.ProbeAddress, candidates, tcpConcurrency, cancellationToken))
                    {
                        openPorts = [];
                    }
                    else
                    {
                        openPorts = await FindOpenTcpPortsAsync(
                            host.ProbeAddress,
                            candidates,
                            tcpConcurrency,
                            cancellationToken,
                            LanConnectTimeout);
                    }
                }
                else
                {
                    openPorts = candidates;
                }

                var checkedCount = progressState.IncrementCheckedCount();
                if (openPorts.Count > 0)
                {
                    progressState.IncrementLiveCount();
                }

                if (checkedCount == progressState.TotalHostCount || checkedCount % 16 == 0)
                {
                    progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                        $"Swept {checkedCount}/{progressState.TotalHostCount} LAN address(es), {progressState.LiveCount} with open well-known port(s), {progressState.FoundCount} HTTP/S service(s).",
                        checkedCount,
                        progressState.TotalHostCount,
                        progressState.FoundCount));
                }
            }
            finally
            {
                hostConcurrency.Release();
            }

            if (openPorts.Count == 0)
            {
                return;
            }

            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Host {host.ProbeAddressName}: {openPorts.Count} well-known TCP port(s) open — probing HTTP/S.",
                progressState.CheckedCount,
                progressState.TotalHostCount,
                progressState.FoundCount));

            var priorityPorts = openPorts.Where(CommonHomelabPortSet.Contains).ToArray();
            var remainingPorts = openPorts.Where(port => !CommonHomelabPortSet.Contains(port)).ToArray();
            var probedPorts = new ConcurrentDictionary<int, byte>();
            var redirectPorts = new ConcurrentBag<int>();

            await ProbePortSetAsync(
                host,
                priorityPorts,
                probedPorts,
                redirectPorts,
                serviceConcurrency,
                results,
                progressState,
                progress,
                cancellationToken);

            if (remainingPorts.Length > 0)
            {
                await ProbePortSetAsync(
                    host,
                    remainingPorts,
                    probedPorts,
                    redirectPorts,
                    serviceConcurrency,
                    results,
                    progressState,
                    progress,
                    cancellationToken);
            }

            await FollowRedirectPortsAsync(
                host,
                redirectPorts,
                probedPorts,
                tcpConcurrency,
                serviceConcurrency,
                results,
                progressState,
                progress,
                cancellationToken);
        }

        private static async Task<bool> IsLanHostReachableAsync(
            IPAddress address,
            IReadOnlyList<int> candidatePorts,
            SemaphoreSlim tcpConcurrency,
            CancellationToken cancellationToken)
        {
            if (await CanPingAsync(address, cancellationToken))
            {
                return true;
            }

            // Probe common ports first (fast reject for empty IPs), then alternate HTTPS / expanded.
            var common = candidatePorts.Where(CommonHomelabPortSet.Contains).ToArray();
            if (common.Length > 0 &&
                await CanOpenAnyTcpAsync(address, common, tcpConcurrency, cancellationToken, LanConnectTimeout))
            {
                return true;
            }

            var remaining = candidatePorts.Where(port => !CommonHomelabPortSet.Contains(port)).ToArray();
            return remaining.Length > 0 &&
                   await CanOpenAnyTcpAsync(address, remaining, tcpConcurrency, cancellationToken, LanConnectTimeout);
        }

        private static async Task ProbePortSetAsync(
            ProbeHost host,
            IReadOnlyList<int> ports,
            ConcurrentDictionary<int, byte> probedPorts,
            ConcurrentBag<int> redirectPorts,
            SemaphoreSlim serviceConcurrency,
            ConcurrentBag<DiscoveryEvidence> results,
            HostDiscoveryProgressState progressState,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (ports.Count == 0)
            {
                return;
            }

            await Task.WhenAll(ports.Select(port => ProbeServicePortAsync(
                host,
                port,
                probedPorts,
                redirectPorts,
                serviceConcurrency,
                results,
                progressState,
                progress,
                cancellationToken)));
        }

        private static async Task FollowRedirectPortsAsync(
            ProbeHost host,
            ConcurrentBag<int> redirectPorts,
            ConcurrentDictionary<int, byte> probedPorts,
            SemaphoreSlim tcpConcurrency,
            SemaphoreSlim serviceConcurrency,
            ConcurrentBag<DiscoveryEvidence> results,
            HostDiscoveryProgressState progressState,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            var pending = new List<int>();
            while (redirectPorts.TryTake(out var port) && pending.Count < MaxRedirectFollowPortsPerHost)
            {
                if (!probedPorts.ContainsKey(port) && !pending.Contains(port))
                {
                    pending.Add(port);
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            IReadOnlyList<int> openPending = host.ProbeAddress is null
                ? pending
                : await FindOpenTcpPortsAsync(host.ProbeAddress, pending, tcpConcurrency, cancellationToken);

            if (openPending.Count == 0)
            {
                return;
            }

            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"Live host {host.ProbeAddressName}; following {openPending.Count} redirect-discovered port(s).",
                progressState.CheckedCount,
                progressState.TotalHostCount,
                progressState.FoundCount));

            // Do not chain further redirects from this pass — one hop is enough to catch moved UIs.
            var ignoredNested = new ConcurrentBag<int>();
            await ProbePortSetAsync(
                host,
                openPending,
                probedPorts,
                ignoredNested,
                serviceConcurrency,
                results,
                progressState,
                progress,
                cancellationToken);
        }

        private static async Task ProbeServicePortAsync(
            ProbeHost host,
            int port,
            ConcurrentDictionary<int, byte> probedPorts,
            ConcurrentBag<int> redirectPorts,
            SemaphoreSlim serviceConcurrency,
            ConcurrentBag<DiscoveryEvidence> results,
            HostDiscoveryProgressState progressState,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            if (!probedPorts.TryAdd(port, 0))
            {
                return;
            }

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
                    foreach (var redirectPort in ExtractSameHostRedirectPorts(host, evidence))
                    {
                        redirectPorts.Add(redirectPort);
                    }

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
            // Keep every loopback listener — do not filter through the allowlist or services like
            // UniFi OS (:11443) are discarded before HTTP probing can run.
            var ports = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .Where(port => port is > 0 and <= 65535)
                .Distinct()
                .Order()
                .ToArray();

            return ports.Length == 0
                ? []
                : [new ProbeHost("localhost", IPAddress.Loopback, "localhost", "Localhost", "127.0.0.1", "Local host", true, true, ports)];
        }

        private static void ReportLanScanPlan(
            LanScanPlan scanPlan,
            DiscoverySettings settings,
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress)
        {
            if (progress is null)
            {
                return;
            }

            var source = scanPlan.Cidrs.Count > 0
                ? $"CIDR(s): {FormatCidrs(scanPlan.Cidrs)}"
                : settings.HasSupervisorToken
                    ? "no Supervisor/configured CIDR; using local interface fallback"
                    : "no Supervisor token or configured CIDR; using local interface fallback";
            var fallback = scanPlan.LocalInterfaceFallbackCount > 0
                ? $", {scanPlan.LocalInterfaceFallbackCount} fallback interface target(s)"
                : string.Empty;

            progress.Report(new LocalHttpServiceDiscoveryProgressUpdate(
                $"LAN scan plan: {source}; {scanPlan.KnownHosts.Count} ARP/neighbour host(s), {scanPlan.ScanHosts.Count} targeted host(s){fallback}.",
                0,
                scanPlan.KnownHosts.Count + scanPlan.ScanHosts.Count,
                0));
        }

        private static string FormatCidrs(IReadOnlyList<string> cidrs)
        {
            const int maxShown = 4;
            var shown = string.Join(", ", cidrs.Take(maxShown));
            return cidrs.Count <= maxShown ? shown : $"{shown}, +{cidrs.Count - maxShown} more";
        }

        private static async Task<LanScanPlan> BuildLanScanPlanAsync(DiscoverySettings settings, CancellationToken cancellationToken)
        {
            var knownAddresses = await LoadLanNeighbourAddressesAsync(cancellationToken);
            var supervisorCidrs = await LoadSupervisorLanCidrsAsync(settings, cancellationToken);
            var configuredCidrs = settings.Cidrs
                .Concat(supervisorCidrs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // Always expand supervisor/configured CIDRs AND local interface subnets so Scan
            // covers every LAN IP, not only ARP neighbours.
            var cidrAddresses = configuredCidrs.Length > 0 ? ExpandCidrs(configuredCidrs) : Array.Empty<IPAddress>();
            var localInterfaceAddresses = ExpandLocalInterfaceCidrs();
            var neighbourAddresses = knownAddresses
                .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
                .Where(address => address is not null)
                .Cast<IPAddress>();
            var addresses = neighbourAddresses
                .Concat(cidrAddresses)
                .Concat(localInterfaceAddresses);

            var ports = BuildBaseScanPorts(settings);
            var localAddresses = GetLocalIPv4Interfaces()
                .Select(item => item.Address)
                .ToHashSet(IPAddressComparer.Instance);
            var hosts = addresses
                .Where(IsPrivateIPv4)
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
                // Prefer ARP/neighbours and same-/24 as the host NIC; hard-cap so a /16 never
                // schedules tens of thousands of TCP sweeps.
                .OrderByDescending(host => host.IsKnownLive)
                .ThenBy(host => host.ProbeAddress is null
                    ? uint.MaxValue
                    : DistanceToNearestLocalNetwork(host.ProbeAddress, localAddresses))
                .ThenBy(host => host.ProbeAddress is null ? uint.MaxValue : AddressToUInt32(host.ProbeAddress))
                .Take(MaxLanScanAddresses)
                .ToArray();

            return new LanScanPlan(
                hosts.Where(host => host.IsKnownLive).ToArray(),
                hosts.Where(host => !host.IsKnownLive).ToArray(),
                configuredCidrs,
                localInterfaceAddresses.Count);
        }

        private static uint DistanceToNearestLocalNetwork(IPAddress address, IReadOnlyCollection<IPAddress> localAddresses)
        {
            if (localAddresses.Count == 0)
            {
                return AddressToUInt32(address);
            }

            var value = AddressToUInt32(address);
            var best = uint.MaxValue;
            foreach (var local in localAddresses)
            {
                var localValue = AddressToUInt32(local);
                // Prefer same Class-C neighbourhood as the HA host interface.
                var sameClassC = (value & 0xFFFFFF00) == (localValue & 0xFFFFFF00) ? 0u : 1u;
                var distance = sameClassC << 24 | (value > localValue ? value - localValue : localValue - value);
                if (distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }

        private static async Task<IReadOnlyList<ProbeHost>> DiscoverSsdpHostsAsync(
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate("SSDP discovery: listening for HTTP service advertisements.", 0, 0, 0));
            const string request = "M-SEARCH * HTTP/1.1\r\n" +
                                   "HOST: 239.255.255.250:1900\r\n" +
                                   "MAN: \"ssdp:discover\"\r\n" +
                                   "MX: 1\r\n" +
                                   "ST: ssdp:all\r\n" +
                                   "USER-AGENT: LinuxMadeSane/1.0 EdgeGateway/1.0\r\n\r\n";

            try
            {
                using var client = CreateUdpDiscoveryClient();
                var payload = Encoding.ASCII.GetBytes(request);
                await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
                var responses = await ReceiveUdpResponsesAsync(client, MulticastDiscoveryTimeout, cancellationToken);
                return responses
                    .SelectMany(response => ParseSsdpProbeHosts(response.Buffer))
                    .DistinctBy(BuildProbeHostKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return [];
            }
        }

        private static IEnumerable<ProbeHost> ParseSsdpProbeHosts(byte[] payload)
        {
            var text = Encoding.UTF8.GetString(payload);
            var location = ReadHeaderValue(text, "LOCATION");
            if (string.IsNullOrWhiteSpace(location))
            {
                yield break;
            }

            var displayName = FirstNonBlank(ReadHeaderValue(text, "SERVER"), ReadHeaderValue(text, "ST"), "SSDP device");
            var host = TryBuildProbeHostFromUrl(location, "SSDP", displayName);
            if (host is not null)
            {
                yield return host;
            }
        }

        private static async Task<IReadOnlyList<ProbeHost>> DiscoverMdnsHostsAsync(
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate("mDNS discovery: querying local HTTP service records.", 0, 0, 0));
            try
            {
                using var client = CreateUdpDiscoveryClient();
                var payload = BuildMdnsQuery(
                    "_http._tcp.local",
                    "_https._tcp.local",
                    "_home-assistant._tcp.local",
                    "_hap._tcp.local");
                await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
                var responses = await ReceiveUdpResponsesAsync(client, MulticastDiscoveryTimeout, cancellationToken);
                return responses
                    .SelectMany(response => ParseMdnsProbeHosts(response.Buffer))
                    .DistinctBy(BuildProbeHostKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return [];
            }
        }

        private static async Task<IReadOnlyList<ProbeHost>> DiscoverWsDiscoveryHostsAsync(
            IProgress<LocalHttpServiceDiscoveryProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new LocalHttpServiceDiscoveryProgressUpdate("WS-Discovery: probing network devices for HTTP addresses.", 0, 0, 0));
            var messageId = Guid.NewGuid();
            var request = $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                            xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                            xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
                  <e:Header>
                    <w:MessageID>uuid:{{messageId}}</w:MessageID>
                    <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
                    <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
                  </e:Header>
                  <e:Body>
                    <d:Probe />
                  </e:Body>
                </e:Envelope>
                """;

            try
            {
                using var client = CreateUdpDiscoveryClient();
                var payload = Encoding.UTF8.GetBytes(request);
                await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702));
                var responses = await ReceiveUdpResponsesAsync(client, MulticastDiscoveryTimeout, cancellationToken);
                return responses
                    .SelectMany(response => ParseWsDiscoveryProbeHosts(response.Buffer))
                    .DistinctBy(BuildProbeHostKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return [];
            }
        }

        private static IEnumerable<ProbeHost> ParseWsDiscoveryProbeHosts(byte[] payload)
        {
            var text = Encoding.UTF8.GetString(payload);
            foreach (var url in ExtractWsDiscoveryXAddrs(text))
            {
                var host = TryBuildProbeHostFromUrl(url, "WS-Discovery", "WS-Discovery device");
                if (host is not null)
                {
                    yield return host;
                }
            }
        }

        private static UdpClient CreateUdpDiscoveryClient()
        {
            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            return client;
        }

        private static async Task<IReadOnlyList<UdpReceiveResult>> ReceiveUdpResponsesAsync(
            UdpClient client,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var responses = new List<UdpReceiveResult>();
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(remaining);
                try
                {
                    responses.Add(await client.ReceiveAsync(timeoutSource.Token));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            return responses;
        }

        private static string? ReadHeaderValue(string text, string name)
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
                if (separatorIndex <= 0 ||
                    !line[..separatorIndex].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line[(separatorIndex + 1)..].Trim();
            }

            return null;
        }

        private static IEnumerable<string> ExtractWsDiscoveryXAddrs(string text)
        {
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(
                    new StringReader(text),
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null
                    });
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch
            {
                yield break;
            }

            foreach (var element in document.Descendants().Where(element =>
                         element.Name.LocalName.Equals("XAddrs", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var value in element.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return value;
                }
            }
        }

        private static ProbeHost? TryBuildProbeHostFromUrl(string value, string scope, string? displayName)
        {
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not "http" and not "https" ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            var port = uri.IsDefaultPort ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80 : uri.Port;
            if (port is <= 0 or > 65535)
            {
                return null;
            }

            var host = uri.Host.Trim('[', ']');
            var address = IPAddress.TryParse(host, out var parsedAddress) && parsedAddress.AddressFamily == AddressFamily.InterNetwork
                ? parsedAddress
                : null;
            return new ProbeHost(
                host,
                address,
                host,
                scope,
                address?.ToString(),
                displayName,
                false,
                true,
                [port]);
        }

        private static byte[] BuildMdnsQuery(params string[] names)
        {
            var bytes = new List<byte>(512);
            WriteUInt16(bytes, 0);
            WriteUInt16(bytes, 0);
            WriteUInt16(bytes, (ushort)names.Length);
            WriteUInt16(bytes, 0);
            WriteUInt16(bytes, 0);
            WriteUInt16(bytes, 0);
            foreach (var name in names)
            {
                foreach (var label in name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var labelBytes = Encoding.ASCII.GetBytes(label);
                    bytes.Add((byte)Math.Min(labelBytes.Length, 63));
                    bytes.AddRange(labelBytes.Take(63));
                }

                bytes.Add(0);
                WriteUInt16(bytes, 12);
                WriteUInt16(bytes, 0x8001);
            }

            return bytes.ToArray();
        }

        private static IEnumerable<ProbeHost> ParseMdnsProbeHosts(byte[] payload)
        {
            if (payload.Length < 12)
            {
                yield break;
            }

            var offset = 4;
            var questionCount = ReadUInt16(payload, ref offset);
            var answerCount = ReadUInt16(payload, ref offset);
            var authorityCount = ReadUInt16(payload, ref offset);
            var additionalCount = ReadUInt16(payload, ref offset);
            for (var index = 0; index < questionCount && offset < payload.Length; index++)
            {
                _ = ReadDnsName(payload, ref offset);
                offset += 4;
            }

            var srvRecords = new Dictionary<string, MdnsSrvRecord>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            var recordCount = answerCount + authorityCount + additionalCount;
            for (var index = 0; index < recordCount && offset < payload.Length; index++)
            {
                var name = ReadDnsName(payload, ref offset);
                if (string.IsNullOrWhiteSpace(name) || offset + 10 > payload.Length)
                {
                    yield break;
                }

                var type = ReadUInt16(payload, ref offset);
                _ = ReadUInt16(payload, ref offset);
                offset += 4;
                var dataLength = ReadUInt16(payload, ref offset);
                var dataStart = offset;
                var dataEnd = Math.Min(payload.Length, dataStart + dataLength);
                if (dataEnd < dataStart)
                {
                    yield break;
                }

                if (type == 1 && dataLength == 4)
                {
                    var address = new IPAddress(payload.AsSpan(dataStart, 4));
                    if (IsPrivateIPv4(address))
                    {
                        addresses[name.TrimEnd('.')] = address;
                    }
                }
                else if (type == 33 && dataLength >= 6)
                {
                    var srvOffset = dataStart + 4;
                    var port = ReadUInt16(payload, ref srvOffset);
                    var target = ReadDnsName(payload, ref srvOffset).TrimEnd('.');
                    if (port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(target))
                    {
                        srvRecords[name.TrimEnd('.')] = new MdnsSrvRecord(target, port);
                    }
                }

                offset = dataEnd;
            }

            foreach (var record in srvRecords)
            {
                var serviceName = record.Key;
                var target = record.Value.Target.TrimEnd('.');
                var address = addresses.GetValueOrDefault(target);
                var host = address?.ToString() ?? target;
                var probeHost = new ProbeHost(
                    host,
                    address,
                    host,
                    "mDNS",
                    address?.ToString(),
                    CleanMdnsServiceName(serviceName),
                    false,
                    true,
                    [record.Value.Port]);
                yield return probeHost;
            }
        }

        private static string ReadDnsName(byte[] payload, ref int offset)
        {
            var labels = new List<string>();
            var position = offset;
            var jumped = false;
            var guard = 0;
            while (position < payload.Length && guard++ < 64)
            {
                var length = payload[position++];
                if (length == 0)
                {
                    if (!jumped)
                    {
                        offset = position;
                    }

                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (position >= payload.Length)
                    {
                        break;
                    }

                    var pointer = ((length & 0x3F) << 8) | payload[position++];
                    if (!jumped)
                    {
                        offset = position;
                    }

                    position = pointer;
                    jumped = true;
                    continue;
                }

                if (position + length > payload.Length)
                {
                    break;
                }

                labels.Add(Encoding.UTF8.GetString(payload, position, length));
                position += length;
            }

            return string.Join('.', labels);
        }

        private static ushort ReadUInt16(byte[] payload, ref int offset)
        {
            if (offset + 2 > payload.Length)
            {
                offset = payload.Length;
                return 0;
            }

            var value = (ushort)((payload[offset] << 8) | payload[offset + 1]);
            offset += 2;
            return value;
        }

        private static void WriteUInt16(ICollection<byte> bytes, int value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static string CleanMdnsServiceName(string value)
        {
            var name = value.TrimEnd('.');
            foreach (var suffix in new[] { "._http._tcp.local", "._https._tcp.local", "._home-assistant._tcp.local", "._hap._tcp.local" })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    break;
                }
            }

            return string.IsNullOrWhiteSpace(name) ? "mDNS service" : name.Replace('\\', ' ');
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
                foreach (var cidr in ExtractIpv4Cidrs(ipv4, item))
                {
                    yield return cidr;
                }
            }

            foreach (var cidr in ExtractIpv4Cidrs(item))
            {
                yield return cidr;
            }
        }

        private static IEnumerable<string> ExtractIpv4Cidrs(JsonElement element, params JsonElement[] fallbackContexts)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var cidr in ExtractIpv4Cidrs(item, fallbackContexts))
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

            foreach (var name in new[] { "ip_address", "address", "addresses", "ip", "ipv4_address" })
            {
                if (!element.TryGetProperty(name, out var property))
                {
                    continue;
                }

                if (property.ValueKind == JsonValueKind.String)
                {
                    var cidr = BuildIpv4Cidr(property.GetString(), element, fallbackContexts);
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
                            var cidr = BuildIpv4Cidr(item.GetString(), element, fallbackContexts);
                            if (!string.IsNullOrWhiteSpace(cidr))
                            {
                                yield return cidr;
                            }
                        }
                        else
                        {
                            foreach (var cidr in ExtractIpv4Cidrs(item, PrependContext(element, fallbackContexts)))
                            {
                                yield return cidr;
                            }
                        }
                    }
                }
                else if (property.ValueKind == JsonValueKind.Object)
                {
                    foreach (var cidr in ExtractIpv4Cidrs(property, PrependContext(element, fallbackContexts)))
                    {
                        yield return cidr;
                    }
                }
            }
        }

        private static string? BuildIpv4Cidr(string? value, JsonElement context, JsonElement[] fallbackContexts)
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

            var prefixLength = ReadPrefixLengthFromContexts(context, fallbackContexts) ?? 24;
            return $"{address}/{prefixLength}";
        }

        private static int? ReadPrefixLengthFromContexts(JsonElement context, JsonElement[] fallbackContexts)
        {
            var prefixLength = ReadPrefixLength(context) ?? ReadNetmaskPrefixLength(context);
            if (prefixLength is not null)
            {
                return prefixLength;
            }

            foreach (var fallbackContext in fallbackContexts)
            {
                prefixLength = ReadPrefixLength(fallbackContext) ?? ReadNetmaskPrefixLength(fallbackContext);
                if (prefixLength is not null)
                {
                    return prefixLength;
                }
            }

            return null;
        }

        private static JsonElement[] PrependContext(JsonElement context, JsonElement[] fallbackContexts)
        {
            var contexts = new JsonElement[fallbackContexts.Length + 1];
            contexts[0] = context;
            fallbackContexts.CopyTo(contexts, 1);
            return contexts;
        }

        private static int? ReadPrefixLength(JsonElement element)
        {
            foreach (var name in new[] { "prefix", "prefix_length", "prefixLength", "subnet_prefix", "subnet_prefix_length", "network_prefix", "network_prefix_length", "cidr_prefix", "cidrPrefix", "mask_prefix" })
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
            foreach (var name in new[] { "netmask", "subnet_mask", "network_mask", "mask" })
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
            return new ProbeHost(ip, parsed, ip, "Tailnet", ip, displayName, false, true, CommonHomelabPorts);
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
                    DisplayName: $"{name} app",
                    Notes: [$"Supervisor app slug {slug}.", $"State: {state}.", ingress ? "Ingress is enabled." : "Ingress is not enabled.", port.Description]);
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
                    DisplayName: $"{name} app",
                    Notes: [$"Supervisor app slug {slug}.", $"State: {state}.", $"Ingress port {ingressPort.Value}.", "No exposed host port was reported."]);
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
                    "Docker discovery skipped. Docker API socket is not available to this app.",
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
                        FaviconDataUrl = match.FaviconDataUrl,
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

            var displayName = FingerprintRules.IsUnknownLabel(best.ServiceName)
                ? FirstNonBlank(best.Title, $"{best.Host}:{best.Port}") ?? $"{best.Host}:{best.Port}"
                : FirstNonBlank(best.ServiceName, best.Title, $"{best.Host}:{best.Port}") ?? $"{best.Host}:{best.Port}";
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
                group.SelectMany(item => item.Notes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                FirstNonBlank(group.Select(item => item.FaviconDataUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)), best.FaviconDataUrl));
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
                var pageHtml = await TryReadPageHtmlAsync(response, timeout.Token);
                var title = TryExtractTitle(pageHtml);
                var redirect = response.Headers.Location?.ToString() ?? string.Empty;
                var server = response.Headers.Server.ToString();
                var favicon = await TryReadFaviconAsync(Client, probeUrl, pageHtml, timeout.Token);
                var tlsSubject = scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? await TryReadTlsSubjectAsync(host.ProbeAddressName, port, timeout.Token)
                    : string.Empty;
                var displayHost = await ResolveHostNameAsync(host, cancellationToken);
                var fingerprint = FingerprintRules.Fingerprint(title, server, redirect, favicon.Hash, tlsSubject, port);
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
                    FaviconHash: favicon.Hash,
                    FaviconDataUrl: favicon.DataUrl,
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
            // Identify only from response evidence — never from port number alone.
            var haystack = $"{title} {server} {redirect} {tlsSubject}".ToLowerInvariant();
            return haystack switch
            {
                var text when text.Contains("home assistant", StringComparison.Ordinal) =>
                    Known("Home Assistant", "home-assistant", 99, DiscoveryExposure.Publishable, "home-assistant"),
                var text when text.Contains("portainer", StringComparison.Ordinal) =>
                    Known("Portainer", "portainer", 98, DiscoveryExposure.RequiresManualConfirmation, "portainer"),
                var text when text.Contains("jellyfin", StringComparison.Ordinal) =>
                    Known("Jellyfin", "jellyfin", 94, DiscoveryExposure.Publishable, "jellyfin"),
                var text when text.Contains("emby", StringComparison.Ordinal) =>
                    Known("Emby", "emby", 94, DiscoveryExposure.Publishable, "emby"),
                var text when text.Contains("plex", StringComparison.Ordinal) =>
                    Known("Plex", "plex", 94, DiscoveryExposure.Publishable, "plex"),
                var text when text.Contains("grafana", StringComparison.Ordinal) =>
                    Known("Grafana", "grafana", 90, DiscoveryExposure.RequiresManualConfirmation, "grafana"),
                var text when text.Contains("adguard", StringComparison.Ordinal) =>
                    Known("AdGuard Home", "adguard-home", 90, DiscoveryExposure.RequiresManualConfirmation, "adguard"),
                var text when text.Contains("uptime kuma", StringComparison.Ordinal) =>
                    Known("Uptime Kuma", "uptime-kuma", 90, DiscoveryExposure.Publishable, "uptime-kuma"),
                var text when text.Contains("proxmox", StringComparison.Ordinal) =>
                    Known("Proxmox VE", "proxmox", 94, DiscoveryExposure.RequiresManualConfirmation, "proxmox"),
                var text when text.Contains("truenas", StringComparison.Ordinal) =>
                    Known("TrueNAS", "truenas", 92, DiscoveryExposure.RequiresManualConfirmation, "truenas"),
                var text when text.Contains("unraid", StringComparison.Ordinal) =>
                    Known("Unraid", "unraid", 92, DiscoveryExposure.RequiresManualConfirmation, "unraid"),
                var text when text.Contains("openmediavault", StringComparison.Ordinal) || text.Contains("open media vault", StringComparison.Ordinal) =>
                    Known("OpenMediaVault", "openmediavault", 90, DiscoveryExposure.RequiresManualConfirmation, "openmediavault"),
                var text when text.Contains("pi-hole", StringComparison.Ordinal) || text.Contains("pihole", StringComparison.Ordinal) =>
                    Known("Pi-hole", "pihole", 92, DiscoveryExposure.RequiresManualConfirmation, "pihole"),
                var text when text.Contains("nextcloud", StringComparison.Ordinal) =>
                    Known("Nextcloud", "nextcloud", 92, DiscoveryExposure.Publishable, "nextcloud"),
                var text when text.Contains("gitlab", StringComparison.Ordinal) =>
                    Known("GitLab", "gitlab", 90, DiscoveryExposure.RequiresManualConfirmation, "gitlab"),
                var text when text.Contains("nginx proxy manager", StringComparison.Ordinal) =>
                    Known("Nginx Proxy Manager", "nginx-proxy-manager", 94, DiscoveryExposure.RequiresManualConfirmation, "nginx-proxy-manager"),
                var text when text.Contains("node-red", StringComparison.Ordinal) || text.Contains("nodered", StringComparison.Ordinal) =>
                    Known("Node-RED", "node-red", 90, DiscoveryExposure.RequiresManualConfirmation, "node-red"),
                var text when text.Contains("go2rtc", StringComparison.Ordinal) =>
                    Known("go2rtc", "go2rtc", 90, DiscoveryExposure.RequiresManualConfirmation, "go2rtc"),
                var text when text.Contains("immich", StringComparison.Ordinal) =>
                    Known("Immich", "immich", 94, DiscoveryExposure.Publishable, "immich"),
                var text when text.Contains("gitea", StringComparison.Ordinal) =>
                    Known("Gitea", "gitea", 90, DiscoveryExposure.Publishable, "gitea"),
                var text when text.Contains("forgejo", StringComparison.Ordinal) =>
                    Known("Forgejo", "forgejo", 90, DiscoveryExposure.Publishable, "forgejo"),
                var text when text.Contains("wiki.js", StringComparison.Ordinal) || text.Contains("wikijs", StringComparison.Ordinal) =>
                    Known("Wiki.js", "wikijs", 88, DiscoveryExposure.Publishable, "wikijs"),
                var text when text.Contains("synology", StringComparison.Ordinal) || text.Contains("diskstation", StringComparison.Ordinal) =>
                    Known("Synology DSM", "synology-dsm", 92, DiscoveryExposure.RequiresManualConfirmation, "synology-dsm"),
                var text when text.Contains("technitium", StringComparison.Ordinal) =>
                    Known("Technitium DNS", "technitium-dns", 90, DiscoveryExposure.RequiresManualConfirmation, "technitium-dns"),
                var text when text.Contains("kibana", StringComparison.Ordinal) =>
                    Known("Kibana", "kibana", 90, DiscoveryExposure.RequiresManualConfirmation, "kibana"),
                var text when text.Contains("bazarr", StringComparison.Ordinal) =>
                    Known("Bazarr", "bazarr", 92, DiscoveryExposure.RequiresManualConfirmation, "bazarr"),
                var text when text.Contains("nzbget", StringComparison.Ordinal) =>
                    Known("NZBGet", "nzbget", 90, DiscoveryExposure.RequiresManualConfirmation, "nzbget"),
                var text when text.Contains("moonraker", StringComparison.Ordinal) =>
                    Known("Moonraker", "moonraker", 90, DiscoveryExposure.RequiresManualConfirmation, "moonraker"),
                var text when text.Contains("homebox", StringComparison.Ordinal) =>
                    Known("Homebox", "homebox", 90, DiscoveryExposure.Publishable, "homebox"),
                var text when text.Contains("radarr", StringComparison.Ordinal) =>
                    Known("Radarr", "radarr", 92, DiscoveryExposure.RequiresManualConfirmation, "radarr"),
                var text when text.Contains("paperless", StringComparison.Ordinal) =>
                    Known("Paperless-ngx", "paperless", 90, DiscoveryExposure.Publishable, "paperless"),
                var text when text.Contains("omada", StringComparison.Ordinal) =>
                    Known("Omada Controller", "omada-controller", 90, DiscoveryExposure.RequiresManualConfirmation, "omada-controller"),
                var text when text.Contains("qbittorrent", StringComparison.Ordinal) =>
                    Known("qBittorrent", "qbittorrent", 90, DiscoveryExposure.RequiresManualConfirmation, "qbittorrent"),
                var text when text.Contains("sabnzbd", StringComparison.Ordinal) =>
                    Known("SABnzbd", "sabnzbd", 90, DiscoveryExposure.RequiresManualConfirmation, "sabnzbd"),
                var text when text.Contains("jenkins", StringComparison.Ordinal) =>
                    Known("Jenkins", "jenkins", 90, DiscoveryExposure.RequiresManualConfirmation, "jenkins"),
                var text when text.Contains("zigbee2mqtt", StringComparison.Ordinal) =>
                    Known("Zigbee2MQTT", "zigbee2mqtt", 90, DiscoveryExposure.RequiresManualConfirmation, "zigbee2mqtt"),
                var text when text.Contains("traefik", StringComparison.Ordinal) =>
                    Known("Traefik", "traefik", 88, DiscoveryExposure.RequiresManualConfirmation, "traefik"),
                var text when text.Contains("keycloak", StringComparison.Ordinal) =>
                    Known("Keycloak", "keycloak", 90, DiscoveryExposure.RequiresManualConfirmation, "keycloak"),
                var text when text.Contains("calibre", StringComparison.Ordinal) =>
                    Known("Calibre-Web", "calibre-web", 88, DiscoveryExposure.Publishable, "calibre-web"),
                var text when text.Contains("teamcity", StringComparison.Ordinal) =>
                    Known("TeamCity", "teamcity", 88, DiscoveryExposure.RequiresManualConfirmation, "teamcity"),
                var text when text.Contains("deluge", StringComparison.Ordinal) =>
                    Known("Deluge", "deluge", 88, DiscoveryExposure.RequiresManualConfirmation, "deluge"),
                var text when text.Contains("vault", StringComparison.Ordinal) =>
                    Known("HashiCorp Vault", "vault", 90, DiscoveryExposure.UnsafeToExpose, "vault"),
                var text when text.Contains("syncthing", StringComparison.Ordinal) =>
                    Known("Syncthing", "syncthing", 90, DiscoveryExposure.RequiresManualConfirmation, "syncthing"),
                var text when text.Contains("lidarr", StringComparison.Ordinal) =>
                    Known("Lidarr", "lidarr", 92, DiscoveryExposure.RequiresManualConfirmation, "lidarr"),
                var text when text.Contains("readarr", StringComparison.Ordinal) =>
                    Known("Readarr", "readarr", 92, DiscoveryExposure.RequiresManualConfirmation, "readarr"),
                var text when text.Contains("frigate", StringComparison.Ordinal) =>
                    Known("Frigate", "frigate", 94, DiscoveryExposure.RequiresManualConfirmation, "frigate"),
                var text when text.Contains("sonarr", StringComparison.Ordinal) =>
                    Known("Sonarr", "sonarr", 92, DiscoveryExposure.RequiresManualConfirmation, "sonarr"),
                var text when text.Contains("authentik", StringComparison.Ordinal) =>
                    Known("Authentik", "authentik", 92, DiscoveryExposure.RequiresManualConfirmation, "authentik"),
                var text when text.Contains("minio", StringComparison.Ordinal) =>
                    Known("MinIO", "minio", 90, DiscoveryExposure.RequiresManualConfirmation, "minio"),
                var text when text.Contains("mealie", StringComparison.Ordinal) =>
                    Known("Mealie", "mealie", 90, DiscoveryExposure.Publishable, "mealie"),
                var text when text.Contains("prometheus", StringComparison.Ordinal) =>
                    Known("Prometheus", "prometheus", 90, DiscoveryExposure.RequiresManualConfirmation, "prometheus"),
                var text when text.Contains("cockpit", StringComparison.Ordinal) =>
                    Known("Cockpit", "cockpit", 88, DiscoveryExposure.RequiresManualConfirmation, "cockpit"),
                var text when text.Contains("transmission", StringComparison.Ordinal) =>
                    Known("Transmission", "transmission", 88, DiscoveryExposure.RequiresManualConfirmation, "transmission"),
                var text when text.Contains("authelia", StringComparison.Ordinal) =>
                    Known("Authelia", "authelia", 90, DiscoveryExposure.RequiresManualConfirmation, "authelia"),
                var text when text.Contains("prowlarr", StringComparison.Ordinal) =>
                    Known("Prowlarr", "prowlarr", 92, DiscoveryExposure.RequiresManualConfirmation, "prowlarr"),
                var text when text.Contains("webmin", StringComparison.Ordinal) =>
                    Known("Webmin", "webmin", 88, DiscoveryExposure.RequiresManualConfirmation, "webmin"),
                var text when text.Contains("scrypted", StringComparison.Ordinal) =>
                    Known("Scrypted", "scrypted", 90, DiscoveryExposure.RequiresManualConfirmation, "scrypted"),
                var text when text.Contains("rabbitmq", StringComparison.Ordinal) =>
                    Known("RabbitMQ Management", "rabbitmq", 90, DiscoveryExposure.RequiresManualConfirmation, "rabbitmq"),
                var text when text.Contains("netdata", StringComparison.Ordinal) =>
                    Known("Netdata", "netdata", 90, DiscoveryExposure.RequiresManualConfirmation, "netdata"),
                var text when text.Contains("chromecast", StringComparison.Ordinal) ||
                             text.Contains("google cast", StringComparison.Ordinal) ||
                             text.Contains("eureka", StringComparison.Ordinal) =>
                    Known("Chromecast", "chromecast", 90, DiscoveryExposure.RequiresManualConfirmation, "chromecast"),
                var text when text.Contains("opnsense", StringComparison.Ordinal) =>
                    Known("OPNsense", "opnsense", 92, DiscoveryExposure.RequiresManualConfirmation, "opnsense"),
                var text when text.Contains("pfsense", StringComparison.Ordinal) =>
                    Known("pfSense", "pfsense", 92, DiscoveryExposure.RequiresManualConfirmation, "pfsense"),
                var text when LooksLikeUnifiEvidence(text) =>
                    Known("UniFi", "unifi", 90, DiscoveryExposure.RequiresManualConfirmation, "unifi"),
                var text when text.Contains("octoprint", StringComparison.Ordinal) =>
                    Known("OctoPrint", "octoprint", 90, DiscoveryExposure.RequiresManualConfirmation, "octoprint"),
                var text when text.Contains("docker", StringComparison.Ordinal) && port is 2375 or 2376 =>
                    Known("Docker API", "docker-api", 99, DiscoveryExposure.UnsafeToExpose, "docker-api"),
                _ => Unknown(port)
            };
        }

        private static bool LooksLikeUnifiEvidence(string text) =>
            text.Contains("unifi os", StringComparison.Ordinal) ||
            text.Contains("unifi network", StringComparison.Ordinal) ||
            text.Contains("unifi protect", StringComparison.Ordinal) ||
            text.Contains("unifi connect", StringComparison.Ordinal) ||
            text.Contains("ubiquiti", StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"\bunifi\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string NormalizeServiceKind(params string?[] values)
        {
            var text = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
            if (text.Contains("home assistant", StringComparison.Ordinal)) return "home-assistant";
            if (text.Contains("portainer", StringComparison.Ordinal)) return "portainer";
            if (text.Contains("jellyfin", StringComparison.Ordinal)) return "jellyfin";
            if (text.Contains("emby", StringComparison.Ordinal)) return "emby";
            if (text.Contains("plex", StringComparison.Ordinal)) return "plex";
            if (text.Contains("grafana", StringComparison.Ordinal)) return "grafana";
            if (text.Contains("adguard", StringComparison.Ordinal)) return "adguard-home";
            if (text.Contains("uptime kuma", StringComparison.Ordinal)) return "uptime-kuma";
            if (text.Contains("proxmox", StringComparison.Ordinal)) return "proxmox";
            if (text.Contains("truenas", StringComparison.Ordinal)) return "truenas";
            if (text.Contains("unraid", StringComparison.Ordinal)) return "unraid";
            if (text.Contains("nextcloud", StringComparison.Ordinal)) return "nextcloud";
            if (text.Contains("sonarr", StringComparison.Ordinal)) return "sonarr";
            if (text.Contains("radarr", StringComparison.Ordinal)) return "radarr";
            if (text.Contains("frigate", StringComparison.Ordinal)) return "frigate";
            if (text.Contains("immich", StringComparison.Ordinal)) return "immich";
            if (text.Contains("chromecast", StringComparison.Ordinal)) return "chromecast";
            if (LooksLikeUnifiEvidence(text)) return "unifi";
            if (text.Contains("docker", StringComparison.Ordinal)) return "docker-api";
            return "unknown";
        }

        public static string BuildFriendlyName(string serviceKind, string fallback) => serviceKind switch
        {
            "home-assistant" => "Home Assistant",
            "portainer" => "Portainer",
            "jellyfin" => "Jellyfin",
            "emby" => "Emby",
            "plex" => "Plex",
            "grafana" => "Grafana",
            "adguard-home" => "AdGuard Home",
            "uptime-kuma" => "Uptime Kuma",
            "proxmox" => "Proxmox VE",
            "truenas" => "TrueNAS",
            "unraid" => "Unraid",
            "nextcloud" => "Nextcloud",
            "sonarr" => "Sonarr",
            "radarr" => "Radarr",
            "frigate" => "Frigate",
            "immich" => "Immich",
            "chromecast" => "Chromecast",
            "unifi" => "UniFi",
            "docker-api" => "Docker API",
            "unknown" or "unknown-http" => "Unknown",
            _ => string.IsNullOrWhiteSpace(fallback) || IsUnknownLabel(fallback) ? "Unknown" : fallback
        };

        public static DiscoveryExposure ClassifyExposure(string serviceKind, int confidence) => serviceKind switch
        {
            "docker-api" or "vault" => DiscoveryExposure.UnsafeToExpose,
            "portainer" or "grafana" or "adguard-home" or "proxmox" or "truenas" or "unraid" or "unifi" =>
                DiscoveryExposure.RequiresManualConfirmation,
            "unknown" or "unknown-http" => DiscoveryExposure.RequiresManualConfirmation,
            _ => confidence >= 80 ? DiscoveryExposure.Publishable : DiscoveryExposure.RequiresManualConfirmation
        };

        public static bool IsUnknownLabel(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unknown-http", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unknown http service", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("unknown http", StringComparison.OrdinalIgnoreCase);

        private static FingerprintResult Known(string name, string kind, int confidence, DiscoveryExposure exposure, string fingerprint) =>
            new(name, kind, confidence, exposure, fingerprint, [$"{name} fingerprint matched from HTTP/TLS metadata."]);

        private static FingerprintResult Unknown(int port) =>
            new("Unknown", "unknown", 45, DiscoveryExposure.RequiresManualConfirmation, $"unknown:{port}", ["HTTP/S response found, but no known service fingerprint matched."]);
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
        string? FaviconDataUrl = null,
        string? DisplayName = null,
        string? IpAddress = null,
        IReadOnlyList<string>? Notes = null);

    private sealed record FaviconProbeResult(string Hash, string? DataUrl);

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

    private sealed record DiscoveryStage(string Name, IReadOnlyList<ProbeHost> Hosts);

    private sealed record LanScanPlan(
        IReadOnlyList<ProbeHost> KnownHosts,
        IReadOnlyList<ProbeHost> ScanHosts,
        IReadOnlyList<string> Cidrs,
        int LocalInterfaceFallbackCount);

    private sealed record MdnsSrvRecord(string Target, int Port);

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

    private static IReadOnlyList<int> BuildBaseScanPorts(DiscoverySettings settings)
    {
        var ports = new List<int>(CommonHomelabPorts.Length + HttpsConventionPorts.Length + ExpandedPorts.Length + settings.AdditionalPorts.Count);

        void AddRange(IEnumerable<int> values)
        {
            foreach (var port in values)
            {
                if (port > 0 && !ports.Contains(port))
                {
                    ports.Add(port);
                }
            }
        }

        AddRange(CommonHomelabPorts);
        AddRange(HttpsConventionPorts);
        AddRange(ExpandedPorts);
        AddRange(settings.AdditionalPorts);

        return ports;
    }

    private static IReadOnlyList<int> BuildPortsForHost(ProbeHost host, DiscoverySettings settings)
    {
        var ports = new List<int>(CommonHomelabPorts.Length + HttpsConventionPorts.Length + ExpandedPorts.Length + host.KnownPorts.Count + 8);

        void AddRange(IEnumerable<int> values)
        {
            foreach (var port in values)
            {
                if (port > 0 && !ports.Contains(port))
                {
                    ports.Add(port);
                }
            }
        }

        // Common homelab ports always come first for every IP.
        AddRange(CommonHomelabPorts);
        AddRange(host.KnownPorts);
        // Learn alternate HTTPS admin ports (Nxxx443) without hardcoding a single product port.
        AddRange(HttpsConventionPorts);
        AddRange(ExpandedPorts);
        AddRange(settings.AdditionalPorts);

        return ports;
    }

    private static int[] BuildHttpsConventionPorts()
    {
        var ports = new List<int>(64);
        for (var thousands = 1; thousands <= 64; thousands++)
        {
            var port = thousands * 1000 + 443;
            if (port is > 0 and <= 65535)
            {
                ports.Add(port);
            }
        }

        return ports.ToArray();
    }

    private static IEnumerable<string> GuessSchemes(int port)
    {
        if (PrefersHttps(port))
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
        PrefersHttps(port) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

    private static bool PrefersHttps(int port) =>
        port == 443 ||
        HttpsPreferredPorts.Contains(port) ||
        port % 1000 == 443;

    private static async Task<IReadOnlyList<int>> FindOpenTcpPortsAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        SemaphoreSlim tcpConcurrency,
        CancellationToken cancellationToken,
        TimeSpan? connectTimeout = null)
    {
        var open = new ConcurrentBag<int>();
        var timeout = connectTimeout ?? ConnectTimeout;
        await Task.WhenAll(ports.Distinct().Select(async port =>
        {
            if (await CanOpenTcpWithLimitAsync(address, port, tcpConcurrency, cancellationToken, timeout))
            {
                open.Add(port);
            }
        }));

        return ports.Where(open.Contains).Distinct().ToArray();
    }

    private static IEnumerable<int> ExtractSameHostRedirectPorts(ProbeHost host, DiscoveryEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.RedirectLocation) ||
            !Uri.TryCreate(evidence.RedirectLocation, UriKind.Absolute, out var location) ||
            location.Scheme is not ("http" or "https") ||
            location.Port <= 0 ||
            location.Port == evidence.Port)
        {
            yield break;
        }

        if (!IsSameDiscoveryHost(host, evidence, location.Host))
        {
            yield break;
        }

        yield return location.Port;
    }

    private static bool IsSameDiscoveryHost(ProbeHost host, DiscoveryEvidence evidence, string redirectHost)
    {
        var candidates = new[]
        {
            host.ProbeAddressName,
            host.TargetHost,
            host.IpAddress,
            evidence.Host,
            evidence.IpAddress
        };

        return candidates.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            candidate.Equals(redirectHost, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> CanOpenTcpAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken,
        TimeSpan? connectTimeout = null)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout ?? ConnectTimeout);
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
        CancellationToken cancellationToken,
        TimeSpan? connectTimeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = ports
            .Distinct()
            .Select(port => CanOpenTcpWithLimitAsync(address, port, tcpConcurrency, linked.Token, connectTimeout))
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
        CancellationToken cancellationToken,
        TimeSpan? connectTimeout = null)
    {
        try
        {
            await tcpConcurrency.WaitAsync(cancellationToken);
            try
            {
                return await CanOpenTcpAsync(address, port, cancellationToken, connectTimeout);
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

    private static async Task<string?> TryReadPageHtmlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
            return read <= 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = TitleRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()) : null;
    }

    private static async Task<FaviconProbeResult> TryReadFaviconAsync(
        HttpClient client,
        string baseUrl,
        string? pageHtml,
        CancellationToken cancellationToken)
    {
        foreach (var candidateUrl in BuildFaviconCandidateUrls(baseUrl, pageHtml))
        {
            var result = await TryDownloadFaviconAsync(client, candidateUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Hash) || !string.IsNullOrWhiteSpace(result.DataUrl))
            {
                return result;
            }
        }

        return new FaviconProbeResult(string.Empty, null);
    }

    private static IEnumerable<string> BuildFaviconCandidateUrls(string baseUrl, string? pageHtml)
    {
        yield return $"{baseUrl.TrimEnd('/')}/favicon.ico";

        if (string.IsNullOrWhiteSpace(pageHtml))
        {
            yield break;
        }

        foreach (Match tagMatch in FaviconLinkTagRegex().Matches(pageHtml))
        {
            var hrefMatch = FaviconHrefRegex().Match(tagMatch.Value);
            if (!hrefMatch.Success)
            {
                continue;
            }

            var href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value.Trim());
            if (string.IsNullOrWhiteSpace(href) ||
                href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                yield return absolute.ToString();
                continue;
            }

            if (Uri.TryCreate(new Uri(baseUrl.EndsWith('/') ? baseUrl : $"{baseUrl}/"), href, out var resolved))
            {
                yield return resolved.ToString();
            }
        }
    }

    private static async Task<FaviconProbeResult> TryDownloadFaviconAsync(
        HttpClient client,
        string faviconUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(faviconUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new FaviconProbeResult(string.Empty, null);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(contentType) &&
                !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                // Some servers mislabel ico as text/plain; reject clear HTML/JSON payloads.
                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                {
                    return new FaviconProbeResult(string.Empty, null);
                }
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > 128_000)
            {
                return new FaviconProbeResult(string.Empty, null);
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.Length > MaxFaviconBytes)
            {
                return new FaviconProbeResult(hash, null);
            }

            var mediaType = string.IsNullOrWhiteSpace(contentType) ||
                            contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                            contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)
                ? GuessFaviconMediaType(faviconUrl, bytes)
                : contentType;

            return new FaviconProbeResult(hash, $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
        }
        catch
        {
            return new FaviconProbeResult(string.Empty, null);
        }
    }

    private static string GuessFaviconMediaType(string faviconUrl, byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 4 &&
            ((bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) ||
             (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)))
        {
            return bytes[0] == 0x47 ? "image/gif" : "image/x-icon";
        }

        if (faviconUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            (bytes.Length > 4 && Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 64)).Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            return "image/svg+xml";
        }

        if (faviconUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (faviconUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            faviconUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/jpeg";
        }

        return "image/x-icon";
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
        // Reuse ExpandCidrs so wide masks (/8, /16) get the same Class-C-first / prefix floor
        // as supervisor CIDRs instead of enumerating millions of hosts.
        var cidrs = GetLocalIPv4Interfaces()
            .Select(localInterface =>
            {
                var prefix = PrefixLengthFromIPv4Mask(localInterface.Mask) ?? 24;
                prefix = Math.Clamp(prefix, 16, 30);
                return $"{localInterface.Address}/{prefix}";
            })
            .Where(IsPrivateCidr)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ExpandCidrs(cidrs)
            .Where(IsPrivateIPv4)
            .ToArray();
    }

    private static int? PrefixLengthFromIPv4Mask(IPAddress mask)
    {
        if (mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var value = AddressToUInt32(mask);
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

            var baseValue = AddressToUInt32(network) & MaskForPrefix(prefixLength);
            var broadcastValue = baseValue | ~MaskForPrefix(prefixLength);
            foreach (var candidate in EnumerateCidrHostsInProbeOrder(AddressToUInt32(network), baseValue, broadcastValue, prefixLength))
            {
                result.Add(UInt32ToAddress(candidate));
            }
        }

        return result;
    }

    private static IEnumerable<uint> EnumerateCidrHostsInProbeOrder(
        uint requestedAddressValue,
        uint baseValue,
        uint broadcastValue,
        int prefixLength)
    {
        var emitted = new HashSet<uint>();
        if (prefixLength < 24)
        {
            var localClassCBase = requestedAddressValue & MaskForPrefix(24);
            var localClassCBroadcast = localClassCBase | ~MaskForPrefix(24);
            for (var candidate = Math.Max(baseValue + 1, localClassCBase + 1);
                 candidate < broadcastValue && candidate < localClassCBroadcast;
                 candidate++)
            {
                if (emitted.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        for (var candidate = baseValue + 1; candidate < broadcastValue; candidate++)
        {
            if (emitted.Add(candidate))
            {
                yield return candidate;
            }
        }
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
