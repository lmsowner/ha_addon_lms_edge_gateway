using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LMS.EdgeGateway.Core;

public sealed class SystemDnsNameResolver : IDnsNameResolver
{
    public async Task<string?> ResolvePtrAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address);
            var hostName = entry.HostName?.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(hostName) ? null : hostName;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveForwardAsync(string hostName, CancellationToken cancellationToken = default)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostName, cancellationToken);
            return addresses;
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}

public sealed class PingLanLatencyProbe : ILanLatencyProbe
{
    public async Task<int?> MeasureMillisecondsAsync(
        IPAddress address,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var ping = new Ping();
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    ping.SendAsyncCancel();
                }
                catch
                {
                    // ignored
                }
            });

            var reply = await ping.SendPingAsync(address, Math.Clamp(timeoutMilliseconds, 20, 2000));
            return reply.Status == IPStatus.Success
                ? (int)Math.Clamp(reply.RoundtripTime, 0, int.MaxValue)
                : null;
        }
        catch (PingException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

public sealed class LanClientTrustService(
    IDnsNameResolver dnsNameResolver,
    ILanLatencyProbe latencyProbe) : ILanClientTrustService
{
    public async Task<LanClientTrustResult> EvaluateAsync(
        PublishedApplicationDefinition route,
        string sourceIp,
        string cloudflareConnectingIp,
        CancellationToken cancellationToken = default)
    {
        if (!route.LanTrustEnabled)
        {
            return new LanClientTrustResult(false, "LAN trust is disabled for this route.");
        }

        var cidrs = ParseCidrs(route.LanTrustCidrs).ToArray();
        var suffixes = ParseDnsSuffixes(route.LanTrustDnsSuffixes).ToArray();
        if (cidrs.Length == 0 || suffixes.Length == 0)
        {
            return new LanClientTrustResult(
                false,
                "LAN trust requires at least one trusted LAN CIDR and one DNS suffix.");
        }

        var cloudflareIp = (cloudflareConnectingIp ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(cloudflareIp) &&
            IPAddress.TryParse(cloudflareIp, out var parsedCloudflareIp) &&
            !cidrs.Any(cidr => AddressInCidr(parsedCloudflareIp, cidr.Network, cidr.PrefixLength)))
        {
            return new LanClientTrustResult(
                false,
                "LAN trust is not applied to Cloudflare internet clients.");
        }

        if (!IPAddress.TryParse(sourceIp, out var address))
        {
            return new LanClientTrustResult(false, "LAN trust could not parse the source IP.");
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return new LanClientTrustResult(false, "LAN trust never trusts loopback addresses.");
        }

        if (!cidrs.Any(cidr => AddressInCidr(address, cidr.Network, cidr.PrefixLength)))
        {
            return new LanClientTrustResult(false, "Source IP is outside the trusted LAN CIDR list.");
        }

        var ptrName = await dnsNameResolver.ResolvePtrAsync(address, cancellationToken);
        if (string.IsNullOrWhiteSpace(ptrName))
        {
            return new LanClientTrustResult(false, "No reverse DNS name was found for the source IP.");
        }

        var normalizedHost = NormalizeHostName(ptrName);
        if (!suffixes.Any(suffix => HostMatchesSuffix(normalizedHost, suffix)))
        {
            return new LanClientTrustResult(
                false,
                $"Reverse DNS name '{normalizedHost}' is not under a trusted DNS suffix.");
        }

        if (route.LanTrustRequireForwardConfirm)
        {
            var forwardAddresses = await dnsNameResolver.ResolveForwardAsync(normalizedHost, cancellationToken);
            if (!forwardAddresses.Any(candidate => AddressesEqual(candidate, address)))
            {
                return new LanClientTrustResult(
                    false,
                    $"Forward DNS for '{normalizedHost}' did not resolve back to {address}.");
            }
        }

        int? latencyMs = null;
        if (route.LanTrustMaxLatencyMilliseconds is int maxLatency)
        {
            var clampedMax = Math.Clamp(maxLatency, 1, 2000);
            latencyMs = await latencyProbe.MeasureMillisecondsAsync(address, clampedMax, cancellationToken);
            if (latencyMs is null || latencyMs > clampedMax)
            {
                return new LanClientTrustResult(
                    false,
                    latencyMs is null
                        ? $"LAN latency probe to {address} failed."
                        : $"LAN latency {latencyMs}ms exceeds the configured maximum of {clampedMax}ms.",
                    normalizedHost,
                    latencyMs);
            }
        }

        return new LanClientTrustResult(
            true,
            latencyMs is null
                ? $"Trusted LAN client {normalizedHost} ({address})."
                : $"Trusted LAN client {normalizedHost} ({address}) with {latencyMs}ms latency.",
            normalizedHost,
            latencyMs);
    }

    private static IEnumerable<CidrRange> ParseCidrs(string? value)
    {
        foreach (var item in SplitList(value))
        {
            var slashIndex = item.IndexOf('/');
            if (slashIndex <= 0 ||
                !IPAddress.TryParse(item[..slashIndex], out var network) ||
                !int.TryParse(item[(slashIndex + 1)..], out var prefixLength))
            {
                continue;
            }

            if (network.IsIPv4MappedToIPv6)
            {
                network = network.MapToIPv4();
            }

            var maxPrefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > maxPrefix)
            {
                continue;
            }

            yield return new CidrRange(network, prefixLength);
        }
    }

    private static IEnumerable<string> ParseDnsSuffixes(string? value) =>
        SplitList(value)
            .Select(NormalizeHostName)
            .Where(suffix => suffix.Contains('.', StringComparison.Ordinal) && !suffix.StartsWith('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitList(string? value) =>
        (value ?? string.Empty)
            .Split([',', '\r', '\n', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeHostName(string value) =>
        value.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool HostMatchesSuffix(string hostName, string suffix) =>
        hostName.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        hostName.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase);

    private static bool AddressInCidr(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (network.IsIPv4MappedToIPv6)
        {
            network = network.MapToIPv4();
        }

        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static bool AddressesEqual(IPAddress left, IPAddress right)
    {
        if (left.IsIPv4MappedToIPv6)
        {
            left = left.MapToIPv4();
        }

        if (right.IsIPv4MappedToIPv6)
        {
            right = right.MapToIPv4();
        }

        return left.Equals(right);
    }

    private sealed record CidrRange(IPAddress Network, int PrefixLength);
}
