using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LMS.EdgeGateway.Core;
using Xunit;

namespace LMS.EdgeGateway.Core.Tests;

public sealed class LocalHttpServiceDiscoveryTests
{
    [Fact]
    public void Supervisor_network_info_returns_host_lan_cidrs_and_ignores_internal_docker_networks()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": [
                  {
                    "interface": "eth0",
                    "primary": true,
                    "enabled": true,
                    "connected": true,
                    "ipv4": {
                      "method": "static",
                      "ip_address": "192.168.15.3/24",
                      "gateway": "192.168.15.1"
                    }
                  },
                  {
                    "interface": "hassio",
                    "enabled": true,
                    "connected": true,
                    "ipv4": {
                      "ip_address": "172.30.32.1/23"
                    }
                  }
                ]
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["192.168.15.3/24"], cidrs);
    }

    [Fact]
    public void Supervisor_network_info_supports_legacy_interface_object_shape()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": {
                  "enp3s0": {
                    "ip_address": "10.20.30.40/24",
                    "gateway": "10.20.30.1",
                    "primary": true
                  }
                }
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["10.20.30.40/24"], cidrs);
    }

    [Fact]
    public void Supervisor_network_info_combines_address_with_separate_prefix()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": [
                  {
                    "interface": "eth0",
                    "enabled": true,
                    "connected": true,
                    "ipv4": {
                      "address": "192.168.15.3",
                      "prefix": 24
                    }
                  }
                ]
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["192.168.15.3/24"], cidrs);
    }

    [Fact]
    public void Supervisor_network_info_combines_address_with_netmask()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": [
                  {
                    "interface": "enp3s0",
                    "enabled": true,
                    "connected": true,
                    "ipv4": {
                      "ip_address": "10.20.30.40",
                      "subnet_mask": "255.255.254.0"
                    }
                  }
                ]
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["10.20.30.40/23"], cidrs);
    }

    [Fact]
    public void Supervisor_network_info_uses_parent_interface_prefix_for_nested_ipv4_address()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": [
                  {
                    "interface": "enp3s0",
                    "enabled": true,
                    "connected": true,
                    "prefix": 20,
                    "ipv4": {
                      "ip_address": "192.168.15.3"
                    }
                  }
                ]
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["192.168.15.3/20"], cidrs);
    }

    [Fact]
    public void Supervisor_network_info_uses_parent_interface_netmask_for_nested_ipv4_address()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": [
                  {
                    "interface": "enp3s0",
                    "enabled": true,
                    "connected": true,
                    "netmask": "255.255.240.0",
                    "ipv4": {
                      "address": "192.168.15.3"
                    }
                  }
                ]
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["192.168.15.3/20"], cidrs);
    }

    [Fact]
    public void Supervisor_network_info_defaults_bare_private_address_to_24()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "interfaces": [
                  {
                    "interface": "eth0",
                    "enabled": true,
                    "connected": true,
                    "ipv4": {
                      "ip_address": "192.168.15.3"
                    }
                  }
                ]
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var cidrs = ExtractSupervisorLanCidrs(document.RootElement);

        Assert.Equal(["192.168.15.3/24"], cidrs);
    }

    [Fact]
    public void Cidr_expansion_scans_full_20_subnet()
    {
        var addresses = ExpandCidrs(["192.168.15.3/20"]);
        var addressText = addresses.Select(address => address.ToString()).ToArray();

        Assert.Equal(4094, addresses.Count);
        Assert.Equal("192.168.15.1", addressText.First());
        Assert.Equal("192.168.15.254", addressText[253]);
        Assert.Contains("192.168.0.1", addressText);
        Assert.Contains("192.168.8.40", addressText);
    }

    [Fact]
    public void Cidr_expansion_treats_supervisor_host_address_as_network_member()
    {
        var addresses = ExpandCidrs(["192.168.15.2/30"]);

        Assert.Equal(["192.168.15.1", "192.168.15.2"], addresses.Select(address => address.ToString()));
    }

    [Fact]
    public void Common_homelab_ports_include_priority_services_and_stay_ahead_of_known_extras()
    {
        var commonField = typeof(LocalHttpServiceDiscoveryService)
            .GetField("CommonHomelabPorts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(commonField);
        var commonPorts = Assert.IsType<int[]>(commonField.GetValue(null));

        Assert.Contains(8123, commonPorts);
        Assert.Contains(8006, commonPorts);
        Assert.Contains(8096, commonPorts);
        Assert.Contains(8989, commonPorts);
        Assert.Contains(32400, commonPorts);
        Assert.Equal(80, commonPorts[0]);
        Assert.Equal(81, commonPorts[1]);
        Assert.Equal(443, commonPorts[2]);

        var method = typeof(LocalHttpServiceDiscoveryService)
            .GetMethod("BuildPortsForHost", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var probeHostType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("ProbeHost", BindingFlags.NonPublic);
        Assert.NotNull(probeHostType);
        var host = Activator.CreateInstance(
            probeHostType,
            "192.168.1.10",
            IPAddress.Parse("192.168.1.10"),
            "192.168.1.10",
            "LAN",
            "192.168.1.10",
            null,
            false,
            true,
            (IReadOnlyList<int>)[65500]);
        Assert.NotNull(host);

        var settingsType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("DiscoverySettings", BindingFlags.NonPublic);
        Assert.NotNull(settingsType);
        var settings = Activator.CreateInstance(
            settingsType,
            false,
            false,
            (IReadOnlyList<string>)[],
            (IReadOnlyList<int>)[],
            string.Empty);
        Assert.NotNull(settings);

        var ports = Assert.IsAssignableFrom<IReadOnlyList<int>>(method.Invoke(null, [host, settings])).ToArray();
        Assert.Equal(commonPorts.Take(3), ports.Take(3));
        Assert.Contains(65500, ports);
        Assert.True(Array.IndexOf(ports, 8123) < Array.IndexOf(ports, 65500));
        Assert.Contains(11443, ports);
    }

    [Fact]
    public void Https_convention_ports_cover_alternate_admin_https_without_product_hardcoding()
    {
        var field = typeof(LocalHttpServiceDiscoveryService)
            .GetField("HttpsConventionPorts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var ports = Assert.IsType<int[]>(field.GetValue(null));

        Assert.Contains(8443, ports);
        Assert.Contains(10443, ports);
        Assert.Contains(11443, ports);
        Assert.DoesNotContain(80, ports);
        Assert.DoesNotContain(8123, ports);
    }

    [Fact]
    public void Same_host_redirect_ports_are_extracted_for_follow_up_probes()
    {
        var probeHostType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("ProbeHost", BindingFlags.NonPublic);
        Assert.NotNull(probeHostType);
        var host = Activator.CreateInstance(
            probeHostType,
            "192.168.15.20",
            IPAddress.Parse("192.168.15.20"),
            "unifi",
            "LAN",
            "192.168.15.20",
            "unifi",
            false,
            true,
            (IReadOnlyList<int>)[8443]);
        Assert.NotNull(host);

        var evidenceType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("DiscoveryEvidence", BindingFlags.NonPublic);
        Assert.NotNull(evidenceType);
        var evidence = Activator.CreateInstance(
            evidenceType,
            "LAN discovery",
            "LAN",
            "unifi",
            8443,
            "https",
            "UniFi",
            "unifi",
            90,
            DiscoveryExposure.RequiresManualConfirmation,
            true,
            "unifi",
            302,
            "UniFi",
            null,
            "https://unifi:11443/",
            null,
            null,
            null,
            "unifi",
            "192.168.15.20",
            null);
        Assert.NotNull(evidence);

        var method = typeof(LocalHttpServiceDiscoveryService)
            .GetMethod("ExtractSameHostRedirectPorts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var ports = Assert.IsAssignableFrom<IEnumerable<int>>(method.Invoke(null, [host, evidence])).ToArray();
        Assert.Equal([11443], ports);
    }

    [Fact]
    public void Fingerprint_does_not_infer_service_from_port_alone()
    {
        var method = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("FingerprintRules", BindingFlags.NonPublic)!
            .GetMethod("Fingerprint", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null, null, null, null, null, 8443]);
        Assert.NotNull(result);
        Assert.Equal("Unknown", GetProperty<string>(result, "Name"));
        Assert.Equal("unknown", GetProperty<string>(result, "Kind"));

        var unifi = method.Invoke(null, ["UniFi OS", null, null, null, null, 8008]);
        Assert.NotNull(unifi);
        Assert.Equal("UniFi", GetProperty<string>(unifi, "Name"));

        var cast = method.Invoke(null, ["Chromecast", null, null, null, null, 8443]);
        Assert.NotNull(cast);
        Assert.Equal("Chromecast", GetProperty<string>(cast, "Name"));
    }

    [Theory]
    [InlineData("Home Assistant", 0)]
    [InlineData("Proxmox Virtual Environment", 0)]
    [InlineData(null, 2)]
    [InlineData("", 2)]
    [InlineData("   ", 2)]
    [InlineData("404 Not Found", 2)]
    [InlineData("302 Found", 2)]
    [InlineData("Error", 2)]
    [InlineData("403 Forbidden", 2)]
    [InlineData("HTTP 500 Internal Server Error", 2)]
    [InlineData("Unauthorized", 2)]
    public void Title_quality_rank_prefers_real_titles_over_empty_or_error_pages(string? title, int expectedRank)
    {
        Assert.Equal(expectedRank, LocalHttpServiceDiscoveryRanking.TitleQualityRank(title));
    }

    [Fact]
    public void Presentation_rank_orders_titles_then_favicon_then_bare_then_errors()
    {
        var titled = new LocalHttpServiceEndpoint(
            "http://192.168.1.10:8123", "http", "homeassistant.local", 8123, 200, "Home Assistant", null,
            Scope: "LAN", IpAddress: "192.168.1.10", ServiceName: "Home Assistant", ServiceKind: "home-assistant");
        var faviconOnly = new LocalHttpServiceEndpoint(
            "http://192.168.1.20:80", "http", "gadget.local", 80, 200, null, null,
            Scope: "LAN", IpAddress: "192.168.1.20", ServiceName: "Unknown", ServiceKind: "unknown",
            FaviconDataUrl: "data:image/png;base64,abc");
        var bare = new LocalHttpServiceEndpoint(
            "http://192.168.1.21:80", "http", "bare.local", 80, 200, null, null,
            Scope: "LAN", IpAddress: "192.168.1.21", ServiceName: "Unknown", ServiceKind: "unknown");
        var redirect = new LocalHttpServiceEndpoint(
            "http://192.168.1.30:80", "http", "redir.local", 80, 302, "302 Found", null,
            Scope: "LAN", IpAddress: "192.168.1.30", ServiceName: "Unknown", ServiceKind: "unknown");

        Assert.Equal(0, LocalHttpServiceDiscoveryRanking.PresentationRank(titled));
        Assert.Equal(1, LocalHttpServiceDiscoveryRanking.PresentationRank(faviconOnly));
        Assert.Equal(2, LocalHttpServiceDiscoveryRanking.PresentationRank(bare));
        Assert.Equal(3, LocalHttpServiceDiscoveryRanking.PresentationRank(redirect));

        var method = typeof(LocalHttpServiceDiscoveryService)
            .GetMethod("SortEndpoints", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var sorted = Assert.IsAssignableFrom<IReadOnlyList<LocalHttpServiceEndpoint>>(
            method.Invoke(null, [new[] { redirect, bare, faviconOnly, titled }]));

        Assert.Equal("Home Assistant", sorted[0].Title);
        Assert.Null(sorted[1].Title);
        Assert.False(string.IsNullOrWhiteSpace(sorted[1].FaviconDataUrl));
        Assert.Null(sorted[2].Title);
        Assert.True(string.IsNullOrWhiteSpace(sorted[2].FaviconDataUrl));
        Assert.Equal("302 Found", sorted[3].Title);
    }

    [Fact]
    public void Sort_endpoints_places_good_titles_before_empty_and_error_titles()
    {
        var good = new LocalHttpServiceEndpoint(
            "http://192.168.1.10:8123",
            "http",
            "homeassistant.local",
            8123,
            200,
            "Home Assistant",
            null,
            Scope: "LAN",
            IpAddress: "192.168.1.10",
            Confidence: 50);

        var empty = new LocalHttpServiceEndpoint(
            "http://192.168.1.20:8080",
            "http",
            "192.168.1.20",
            8080,
            200,
            null,
            null,
            Scope: "LAN",
            IpAddress: "192.168.1.20",
            Confidence: 90);

        var error = new LocalHttpServiceEndpoint(
            "http://192.168.1.30:80",
            "http",
            "192.168.1.30",
            80,
            404,
            "404 Not Found",
            null,
            Scope: "LAN",
            IpAddress: "192.168.1.30",
            Confidence: 95);

        var method = typeof(LocalHttpServiceDiscoveryService)
            .GetMethod("SortEndpoints", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var sorted = Assert.IsAssignableFrom<IReadOnlyList<LocalHttpServiceEndpoint>>(
            method.Invoke(null, [new[] { error, empty, good }]));

        Assert.Equal("Home Assistant", sorted[0].Title);
        Assert.Null(sorted[1].Title);
        Assert.Equal("404 Not Found", sorted[2].Title);
    }

    [Fact]
    public void Ws_discovery_uses_xaddrs_and_ignores_schema_urls()
    {
        const string payload = """
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:o="http://docs.oasis-open.org/wsn/b-2"
                        xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                        xsi:schemaLocation="http://schemas.microsoft.com/windows/pnpx/2005/10 http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <e:Header>
                <a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</a:Action>
              </e:Header>
              <e:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <a:EndpointReference>
                      <a:Address>urn:uuid:7a0cba40-b61f-11ee-a506-0242ac120002</a:Address>
                    </a:EndpointReference>
                    <d:XAddrs>http://192.168.15.50/onvif/device_service https://192.168.15.51:8443/ws</d:XAddrs>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </e:Body>
            </e:Envelope>
            """;

        var hosts = ParseWsDiscoveryProbeHosts(Encoding.UTF8.GetBytes(payload));
        var targets = hosts.Select(host => GetProperty<string>(host, "TargetHost") ?? string.Empty).ToArray();
        var ports = hosts.Select(host => GetProperty<IReadOnlyList<int>>(host, "KnownPorts")!.Single()).ToArray();

        Assert.Equal(["192.168.15.50", "192.168.15.51"], targets);
        Assert.Equal([80, 8443], ports);
        Assert.DoesNotContain(hosts, host => GetProperty<string>(host, "TargetHost")!.Contains("schemas", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(hosts, host => GetProperty<string>(host, "TargetHost")!.Contains("oasis-open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Supervisor_core_info_uses_reported_ssl_scheme_and_port()
    {
        const string payload = """
            {
              "result": "ok",
              "data": {
                "ssl": true,
                "port": 8123,
                "ip_address": "172.30.32.2"
              }
            }
            """;

        using var document = JsonDocument.Parse(payload);
        var evidence = BuildCoreEvidence(document.RootElement);

        Assert.Equal("homeassistant", GetProperty<string>(evidence, "Host"));
        Assert.Equal("https", GetProperty<string>(evidence, "Scheme"));
        Assert.Equal(8123, GetProperty<int>(evidence, "Port"));
        Assert.Null(GetProperty<string?>(evidence, "IpAddress"));
    }

    private static IReadOnlyList<string> ExtractSupervisorLanCidrs(JsonElement payload)
    {
        var adapterType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("LanDiscoveryAdapter", BindingFlags.NonPublic);
        Assert.NotNull(adapterType);

        var method = adapterType.GetMethod("ExtractSupervisorLanCidrs", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [payload]);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(result);
    }

    private static IReadOnlyList<IPAddress> ExpandCidrs(IReadOnlyList<string> cidrs)
    {
        var method = typeof(LocalHttpServiceDiscoveryService)
            .GetMethod("ExpandCidrs", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [cidrs]);
        return Assert.IsAssignableFrom<IReadOnlyList<IPAddress>>(result);
    }

    private static IReadOnlyList<object> ParseWsDiscoveryProbeHosts(byte[] payload)
    {
        var adapterType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("LanDiscoveryAdapter", BindingFlags.NonPublic);
        Assert.NotNull(adapterType);

        var method = adapterType.GetMethod("ParseWsDiscoveryProbeHosts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [payload]);
        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(result);
        return enumerable.Cast<object>().ToArray();
    }

    private static object BuildCoreEvidence(JsonElement payload)
    {
        var adapterType = typeof(LocalHttpServiceDiscoveryService)
            .GetNestedType("HomeAssistantDiscoveryAdapter", BindingFlags.NonPublic);
        Assert.NotNull(adapterType);

        var method = adapterType.GetMethod("BuildCoreEvidence", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [payload]);
        Assert.NotNull(result);
        return result;
    }

    private static T? GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        Assert.NotNull(property);
        return (T?)property.GetValue(instance);
    }
}
