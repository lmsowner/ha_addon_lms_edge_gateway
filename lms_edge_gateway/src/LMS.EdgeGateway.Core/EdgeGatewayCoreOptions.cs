namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayCoreOptions
{
    public string DataRoot { get; set; } = "data/lms-edge-gateway";
    public string WellKnownPublicRoot { get; set; } = "";
    public string CaddyConfigPath { get; set; } = "data/caddy/Caddyfile";
    public string CloudflaredConfigDirectory { get; set; } = "data/cloudflared";
    public string CloudflaredExecutablePath { get; set; } = "";
    public string OptionsJsonPath { get; set; } = "/data/options.json";
    public string HomeAssistantBaseUrl { get; set; } = "http://supervisor/core";
    public string CaddyLocalServiceUrl { get; set; } = "http://localhost:18080";
    public int LmsForwardAuthPort { get; set; } = 5000;
    public string ManagedTunnelNamePrefix { get; set; } = "linux-made-sane";
    public string ManagedRecordComment { get; set; } = "Managed by Linux Made Sane - Edge Gateway";
    public bool EnableDockerDiscovery { get; set; }
    public bool EnableExpandedLanDiscovery { get; set; }
    public IReadOnlyList<string> DiscoveryCidrs { get; set; } = [];
    public IReadOnlyList<int> DiscoveryPorts { get; set; } = [];
}
