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
