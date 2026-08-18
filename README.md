# LMS Home Assistant Add-ons

This repository contains Linux Made Sane Home Assistant add-ons.

- **LMS Edge Gateway for Home Assistant**: secure self-hosted application publishing through Cloudflare Tunnel, Caddy, and LMS authentication policy.
- **LMS Tesla Fleet Helper**: a companion Tesla Fleet key helper that uses Edge Gateway for public HTTPS publishing.

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
6. Optionally install **LMS Tesla Fleet Helper** after Edge Gateway is running.
7. Start the add-on and open the web UI through Home Assistant ingress.

### Install progress in Home Assistant

If Home Assistant previously showed install progress stuck at `0%` then jumping to finished, that was usually because the Supervisor was building the add-on image locally from source.

From `0.1.60`, LMS Edge Gateway publishes prebuilt images to GHCR and sets the add-on `image` field. Home Assistant should pull:

```text
ghcr.io/lmsowner/lms_edge_gateway-{arch}:<version>
```

instead of compiling .NET/Caddy/cloudflared on the HA host. The progress UI can still be coarse, but installs/updates should be much faster.

If a pull fails with unauthorized/not found, open the matching package under GitHub Packages and set visibility to Public (CI attempts this automatically).

After install, start the add-on and use the Setup tab to validate Cloudflare, cloudflared, Caddy, DNS, and route health.

## Product shape

This repository is a dedicated Home Assistant product, not a fork of LMS.

The Edge Gateway add-on source is split into reusable layers:

- `LMS.Shared`: LMS-style UI primitives, navigation, status pills, panels, and shared contracts.
- `LMS.EdgeGateway.Core`: reusable edge gateway status, configuration, and orchestration abstractions.
- `HA.LMS.EdgeGateway`: Home Assistant-specific Blazor host and ingress surface.

The Home Assistant folder is intentionally thin. HA-specific concerns are packaging, ingress, persistent `/data`, and add-on lifecycle.

The Tesla Fleet Helper is a separate add-on folder. It owns Tesla-specific key generation, Home Assistant key export, Tesla Developer setup values, virtual key URL guidance, and diagnostics. It publishes the public key through Edge Gateway rather than embedding Cloudflare or Caddy management itself.

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

Then open Edge Gateway:

```text
http://localhost:5000
```

The Tesla helper runs locally at:

```text
http://localhost:5055
```
