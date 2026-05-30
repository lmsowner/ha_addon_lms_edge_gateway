# Linux Made Sane - Edge Gateway

Linux Made Sane - Edge Gateway makes secure self-hosted application publishing sane for Home Assistant users.

The add-on is the Home Assistant packaging layer for the Linux Made Sane - Edge Gateway control plane. It runs:

- ASP.NET Core / Blazor UI
- Linux Made Sane - Edge Gateway orchestration services
- Caddy
- cloudflared

## Current setup flow

The add-on provides the installable Home Assistant surface, ingress UI, runtime status, persistent storage, and supervised Caddy/cloudflared processes.

Setup Relay uses the saved Cloudflare token to create or reuse the named tunnel, create the proxied wildcard DNS record for `*.ha-app-relay.<domain>`, update tunnel ingress to Caddy, save the cloudflared connector token, and write the generated Caddy configuration.

Per-application publish, Cloudflare Access policy, LMS authentication policy, rollback, and relay removal flows are the next pieces.

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
