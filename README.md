# LMS Edge Gateway for Home Assistant

LMS Edge Gateway for Home Assistant is a Home Assistant Add-on for secure self-hosted application publishing.

It is designed to let Home Assistant users publish Home Assistant and other LAN applications through Cloudflare, Cloudflare Access, Cloudflare Tunnel, Caddy, and LMS authentication policy without hand-writing reverse proxy or Linux service configuration.

## Project links

- Linux Made Sane: https://www.linuxmadesane.com
- GitHub repository: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Home Assistant add-on repository URL: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Issues and feedback: https://github.com/lmsowner/ha_addon_lms_edge_gateway/issues

## Install from Home Assistant

1. Open Home Assistant.
2. Go to Settings, Add-ons, Add-on Store.
3. Open repositories.
4. Add:

```text
https://github.com/lmsowner/ha_addon_lms_edge_gateway
```

5. Install **LMS Edge Gateway for Home Assistant**.
6. Start the add-on and open the web UI through Home Assistant ingress.

## Product shape

This repository is a dedicated Home Assistant product, not a fork of LMS.

The add-on source is split into reusable layers:

- `LMS.Shared`: LMS-style UI primitives, navigation, status pills, panels, and shared contracts.
- `LMS.EdgeGateway.Core`: reusable edge gateway status, configuration, and orchestration abstractions.
- `HA.LMS.EdgeGateway`: Home Assistant-specific Blazor host and ingress surface.

The Home Assistant folder is intentionally thin. HA-specific concerns are packaging, ingress, persistent `/data`, and add-on lifecycle.

## Current scope

The add-on includes:

- buildable Home Assistant add-on structure
- Blazor UI through ingress
- Caddy process supervision
- cloudflared process supervision
- persistent add-on configuration under `/data`
- local Rider and Docker Compose debugging
- runtime status and diagnostics UI
- Cloudflare token validation and relay setup for the selected zone
- named Cloudflare Tunnel creation/reuse
- proxied wildcard DNS for `*.ha-app-relay.<domain>`
- cloudflared connector token persistence under `/data/lms-edge-gateway`
- generated Caddy config for Edge Gateway traffic

Next wiring covers per-application publish, Cloudflare Access policy, LMS authentication policy, route rollback, and relay removal flows.

## Local development

```bash
dotnet build
dotnet run --project lms_edge_gateway/src/HA.LMS.EdgeGateway
```

Docker Compose:

```bash
docker compose up --build
```

Then open:

```text
http://localhost:5000
```
