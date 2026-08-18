# Linux Made Sane - Edge Gateway

Linux Made Sane - Edge Gateway makes secure self-hosted application publishing sane for Home Assistant users.

The add-on is the Home Assistant packaging layer for the Linux Made Sane - Edge Gateway control plane. It runs:

- ASP.NET Core / Blazor UI
- Linux Made Sane - Edge Gateway orchestration services
- Caddy
- cloudflared

## Project links

- Linux Made Sane: https://www.linuxmadesane.com
- GitHub repository: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Home Assistant add-on repository URL: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Issues and feedback: https://github.com/lmsowner/ha_addon_lms_edge_gateway/issues

## Current setup flow

The add-on provides the installable Home Assistant surface, ingress UI, runtime status, persistent storage, and supervised Caddy/cloudflared processes.

Setup Relay uses the saved Cloudflare token to create or reuse the named tunnel, create the proxied wildcard DNS record for `*.ha-app-relay.<domain>`, update tunnel ingress to Caddy, save the cloudflared connector token, and write the generated Caddy configuration.

Per-application publish, Cloudflare Access policy, LMS authentication policy, rollback, and relay removal flows are the next pieces.

## Home Assistant install progress

From `0.1.60`, the add-on `config.yaml` points at prebuilt GHCR images (`ghcr.io/lmsowner/lms_edge_gateway-{arch}`). Home Assistant should pull those images instead of building the Dockerfile on the HA host.

Supervisor progress can still look coarse, but the long local compile/install phase should be gone. If pulls fail, check that GitHub Actions published the version tag and that the GHCR package visibility is Public.

## Storage

Persistent files live under `/data`:

- `/data/lms-edge-gateway/edge-gateway.json`
- `/data/caddy/Caddyfile`
- `/data/cloudflared`
- `/data/logs`

## Local ports

The Blazor UI listens on port `5000`.

Caddy has a default local health endpoint on port `18080`:

```text
http://addon-host:18080/health
```

## Cloudflare

Cloudflare setup requires:

- a Cloudflare account
- a Cloudflare-managed domain
- a scoped Cloudflare API token

The Setup page guides users through token validation, zone selection, tunnel creation, wildcard DNS routing, cloudflared connector setup, and local Caddy configuration.

## Security model

The add-on uses Home Assistant ingress for the management UI. Published applications will be protected by generated Cloudflare Access and Linux Made Sane - Edge Gateway policy once the application publish flow is wired.

Setup Relay creates only the scoped wildcard relay namespace. Individual application routes still require the publish flow.

## Domain Services / .well-known Manager

Domain Services publishes public `/.well-known/` files and JSON metadata for domains managed by Edge Gateway. It is generic, not Tesla-specific.

Supported first-pass templates include Tesla Fleet, `security.txt`, WebFinger, Apple App Site Association, Android Asset Links, OpenID/OAuth discovery, custom text, and custom JSON.

Definitions are stored in `/data/lms-edge-gateway/well-known/services.json`. Public files are written below `/share/lms-edge-gateway/well-known/public/{domain}/.well-known/...`. Tesla Fleet private keys are stored separately under `/data/lms-edge-gateway/secrets/tesla-fleet/{serviceId}/private-key.pem` and are not served from the public folder.

Generated Caddy routes are public by default, are constrained to `/.well-known/`, and are emitted before normal app routes so they bypass LMS forward-auth unless a service explicitly requires auth.
