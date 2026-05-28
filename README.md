# LMS Edge Gateway Home Assistant Add-on

LMS Edge Gateway is a Home Assistant Add-on for secure self-hosted application publishing.

It is designed to let Home Assistant users publish Home Assistant and other LAN applications through Cloudflare, Cloudflare Access, Cloudflare Tunnel, Caddy, and LMS authentication policy without hand-writing reverse proxy or Linux service configuration.

## Install from Home Assistant

1. Open Home Assistant.
2. Go to Settings, Add-ons, Add-on Store.
3. Open repositories.
4. Add:

```text
https://github.com/lmsowner/ha_addon_lms_edge_gateway
```

5. Install **LMS Edge Gateway**.
6. Start the add-on and open the web UI through Home Assistant ingress.

## Product shape

This repository is a dedicated Home Assistant product, not a fork of LMS.

The add-on source is split into reusable layers:

- `LMS.Shared`: LMS-style UI primitives, navigation, status pills, panels, and shared contracts.
- `LMS.EdgeGateway.Core`: reusable edge gateway status, configuration, and orchestration abstractions.
- `HA.LMS.EdgeGateway`: Home Assistant-specific Blazor host and ingress surface.

The Home Assistant folder is intentionally thin. HA-specific concerns are packaging, ingress, persistent `/data`, and add-on lifecycle.

## Phase 1

The initial add-on includes:

- buildable Home Assistant add-on structure
- Blazor UI through ingress
- Caddy process supervision
- cloudflared process supervision
- persistent add-on configuration under `/data`
- local Rider and Docker Compose debugging
- runtime status and diagnostics UI

Phase 2 wires Cloudflare API automation, named tunnel lifecycle, Caddy route generation, LMS authentication policy, and rollback flows.

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
