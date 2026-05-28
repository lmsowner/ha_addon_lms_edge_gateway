# LMS Edge Gateway

LMS Edge Gateway makes secure self-hosted application publishing sane for Home Assistant users.

The add-on is the Home Assistant packaging layer for the LMS Edge Gateway control plane. It runs:

- ASP.NET Core / Blazor UI
- LMS Edge Gateway orchestration services
- Caddy
- cloudflared

## What Phase 1 does

Phase 1 provides the installable add-on surface, ingress UI, runtime status, persistent storage, and supervised Caddy/cloudflared processes.

Cloudflare API automation, named tunnel provisioning, Caddy route generation, LMS authentication policy, and rollback flows are Phase 2.

## Storage

Persistent files live under `/data`:

- `/data/lms-edge-gateway/edge-gateway.json`
- `/data/caddy/Caddyfile`
- `/data/cloudflared`
- `/data/logs`

## Local ports

The Blazor UI listens on port `5000`.

Caddy has a default local health endpoint on port `8080`:

```text
http://addon-host:8080/health
```

## Cloudflare

Cloudflare setup requires:

- a Cloudflare account
- a Cloudflare-managed domain
- a scoped Cloudflare API token

Phase 2 will guide users through token validation, zone selection, tunnel creation, DNS routing, Access policy, and LMS auth policy.

## Security model

The add-on uses Home Assistant ingress for the management UI. Published applications will be protected by generated Cloudflare Access and LMS Edge Gateway policy.

No public route is created automatically in Phase 1.
