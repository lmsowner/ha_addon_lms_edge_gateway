namespace LMS.EdgeGateway.Core;

public sealed class EdgeGatewayCoreOptions
{
    public string DataRoot { get; set; } = "data/lms-edge-gateway";
    public string CaddyConfigPath { get; set; } = "data/caddy/Caddyfile";
    public string CloudflaredConfigDirectory { get; set; } = "data/cloudflared";
    public string OptionsJsonPath { get; set; } = "/data/options.json";
    public string HomeAssistantBaseUrl { get; set; } = "http://supervisor/core";
}
